using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// ksPlug / ksPlugVR の <c>Character.CtrlBones</c>（キャラごとのボーンの補正）が、体の作り直しで破棄されたボーンを持ち続ける問題の回避策。
    /// KK の kPlugPatcher_InviteChrID の CoordinateChangeStaleRefFix（kPlug の BonesCtrl を FindBones で探し直す）に当たる。
    ///
    /// 【不具合の仕組み（ksPlugVR も同じ。VR は鼻のボーン Spine.nose が1本多い）】
    ///   CtrlBones は初期化（IE_Init）で一度だけ <c>CtrlBones.GetBones(cha, bc)</c> を呼び、背骨・腕・脚のボーン（cf_j_hips など）の Transform を
    ///   <c>Spine</c>・<c>Arm_L</c>・<c>Arm_R</c>・<c>Leg_L</c>・<c>Leg_R</c> の <c>Offsets.trs</c> に持つ。探し直す処理は無い。
    ///   体位ごとの補正（<c>SubscribeToOffset</c>。設定 OffsetOversized の体格差の補正 ExtraSizeOffset、触ったときの Touch など）は、
    ///   その Transform を写した <c>Offsets</c> を一覧に足し、<c>LateUpdate</c> が毎フレームその Transform を動かす（null を確かめない）。
    ///   H の開始時の衣装の読み直しで体が作り直されると、これらがすべて破棄済みになる。
    ///   - <c>CtrlBones.LateUpdate</c> → <c>ApplyLerpOffset</c> で毎フレーム例外（実機ログで 6919件）。
    ///   - <c>AI.IsCloseFromMBoy</c>（男女の Spine.spine1 の距離。ダンスの選択などで使う）で例外（実機ログで 538件）。
    ///
    /// 【回避策】
    ///   - CtrlBones.LateUpdate の前に、背骨（Spine の root・hips・spine1）が破棄されていれば、ksPlug 自身の GetBones を呼び直して探し直す。
    ///     ボーンがまだ揃っていなければ（体の読み込みの途中）、元の値に戻してそのフレームの原型を飛ばし、30フレームおきに探し直す。
    ///   - 補正の一覧（touch / custom / extraSize / motionIkSubst）の破棄された Transform は、探し直す前の同じボーンの新しい Transform に付け替える。
    ///     付け替えられないもの（GetBones 以外で探したボーンなど。破棄された Transform は名前も読めない）は一覧から取り除く（原型は例外で止まるだけ）。
    ///   - AI.IsCloseFromMBoy の前に、見つけた CtrlBones をすべて同じく確かめる。それでも NullReferenceException になったら「近くない」（false）を返す（Finalizer）。
    /// </summary>
    internal static class CtrlBonesStaleRefFix
    {
        private const string Tag = "[ksPlugStaleRefFix][CtrlBones]";
        private const int RetryIntervalFrames = 30;

        private static PropertyInfo _cha;
        private static FieldInfo _isInit;
        private static MethodInfo _getBones;            // static GetBones(ChaControl, CtrlBones)
        private static FieldInfo _offsetsTrs;           // Offsets.trs
        private static FieldInfo _advOffsetsTrs;        // AdvancedOffsets.trs
        /// <summary>ボーンを持つ部位（Spine・Arm_L・Arm_R・Leg_L・Leg_R）のプロパティと、その中の Offsets のフィールド。</summary>
        private static readonly List<KeyValuePair<PropertyInfo, FieldInfo[]>> _parts = new List<KeyValuePair<PropertyInfo, FieldInfo[]>>();
        /// <summary>破棄を確かめる背骨のボーン（Spine の root・hips・spine1）。</summary>
        private static readonly List<FieldInfo> _probeFields = new List<FieldInfo>();
        private static PropertyInfo _spine;
        private static readonly List<FieldInfo> _offsetLists = new List<FieldInfo>();   // List<Offsets>
        private static FieldInfo _advOffsetList;                                        // List<AdvancedOffsets>

        private static readonly HashSet<MonoBehaviour> _instances = new HashSet<MonoBehaviour>(RefComparer<MonoBehaviour>.Instance);
        private static readonly Dictionary<int, int> _retryFrame = new Dictionary<int, int>();
        private static readonly HashSet<int> _missingLogged = new HashSet<int>();
        private static bool _finalizerLogged;

        internal static void TryApply(Harmony harmony, Assembly asm, string root)
        {
            string label = root + ".Character.CtrlBones";
            try
            {
                Type type = asm.GetType(label);
                Type offsets = asm.GetType(label + "+Offsets");
                Type advOffsets = asm.GetType(label + "+AdvancedOffsets");
                MethodInfo lateUpdate = type != null ? AccessTools.Method(type, "LateUpdate", Type.EmptyTypes) : null;
                if (type == null || offsets == null || advOffsets == null || lateUpdate == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {label}（Offsets / AdvancedOffsets / LateUpdate）が見つからないため、回避策を適用しません（ksPlug のバージョン違いの可能性）。");
                    return;
                }
                _cha = AccessTools.Property(type, "cha");
                _isInit = AccessTools.Field(type, "isInit");
                _getBones = AccessTools.Method(type, "GetBones", new[] { typeof(ChaControl), type });
                _offsetsTrs = AccessTools.Field(offsets, "trs");
                _advOffsetsTrs = AccessTools.Field(advOffsets, "trs");
                _spine = AccessTools.Property(type, "Spine");
                _parts.Clear();
                foreach (string name in new[] { "Spine", "Arm_L", "Arm_R", "Leg_L", "Leg_R" })
                {
                    PropertyInfo p = AccessTools.Property(type, name);
                    if (p == null || p.GetGetMethod(true) == null || p.GetSetMethod(true) == null) continue;
                    var fields = new List<FieldInfo>();
                    foreach (FieldInfo f in AccessTools.GetDeclaredFields(p.PropertyType))
                        if (f.FieldType == offsets) fields.Add(f);
                    _parts.Add(new KeyValuePair<PropertyInfo, FieldInfo[]>(p, fields.ToArray()));
                }
                _probeFields.Clear();
                if (_spine != null)
                {
                    foreach (string name in new[] { "root", "hips", "spine1" })
                    {
                        FieldInfo f = AccessTools.Field(_spine.PropertyType, name);
                        if (f != null && f.FieldType == offsets) _probeFields.Add(f);
                    }
                }
                _offsetLists.Clear();
                foreach (string name in new[] { "touchOffsets", "customOffsets", "extraSizeOffsets" })
                {
                    FieldInfo f = AccessTools.Field(type, name);
                    if (f != null && typeof(IList).IsAssignableFrom(f.FieldType)) _offsetLists.Add(f);
                }
                _advOffsetList = AccessTools.Field(type, "motionIkSubstOffset");

                if (_cha == null || _isInit == null || _getBones == null || _offsetsTrs == null || _advOffsetsTrs == null || _spine == null
                    || _parts.Count != 5 || _probeFields.Count != 3 || _offsetLists.Count != 3 || _advOffsetList == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {label} のフィールド・メソッド（cha / isInit / GetBones / Spine・Arm・Leg / 補正の一覧）が見つからないため、回避策を適用しません（ksPlug のバージョン違いの可能性）。");
                    return;
                }
                harmony.Patch(lateUpdate, prefix: new HarmonyMethod(AccessTools.Method(typeof(CtrlBonesStaleRefFix), nameof(LateUpdatePrefix))));
                PatcherPlugin.Logger.LogInfo($"{Tag} {label}.LateUpdate に回避策を適用しました。");

                Type ai = asm.GetType(root + ".Character.AI");
                MethodInfo isClose = ai != null ? AccessTools.PropertyGetter(ai, "IsCloseFromMBoy") : null;
                if (isClose != null && isClose.ReturnType == typeof(bool))
                {
                    harmony.Patch(isClose,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CtrlBonesStaleRefFix), nameof(IsCloseFromMBoyPrefix))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(CtrlBonesStaleRefFix), nameof(IsCloseFromMBoyFinalizer))));
                    PatcherPlugin.Logger.LogInfo($"{Tag} {root}.Character.AI.IsCloseFromMBoy に回避策を適用しました。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {root}.Character.AI.IsCloseFromMBoy が見つからないため、この処理には回避策を適用しません。");
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} {label} への適用に失敗しました: {ex.Message}");
            }
        }

        private static bool Active => !PatcherPlugin.IsQuitting && PatcherPlugin.EnableCtrlBonesStaleRefFix.Value;

        private static bool LateUpdatePrefix(MonoBehaviour __instance)
        {
            if (!Active) return true;
            try
            {
                _instances.Add(__instance);
                if (!EnsureBones(__instance)) return false;
                RemoveStaleOffsets(__instance);
                return true;
            }
            catch (Exception ex)
            {
                LogException("LateUpdate", ex);
                return true;
            }
        }

        private static void IsCloseFromMBoyPrefix()
        {
            if (!Active) return;
            try
            {
                foreach (MonoBehaviour bc in new List<MonoBehaviour>(_instances))
                {
                    if (bc == null) { _instances.Remove(bc); continue; }
                    EnsureBones(bc);
                }
            }
            catch (Exception ex)
            {
                LogException("IsCloseFromMBoy", ex);
            }
        }

        /// <summary>探し直しても、まだボーンが揃っていない（体の読み込みの途中など）ときの例外は、「近くない」として返す。</summary>
        private static Exception IsCloseFromMBoyFinalizer(Exception __exception, ref bool __result)
        {
            if (__exception == null || !(__exception is NullReferenceException) || !Active) return __exception;
            if (!_finalizerLogged)
            {
                _finalizerLogged = true;
                PatcherPlugin.Logger.LogInfo($"{Tag} AI.IsCloseFromMBoy でボーンが読めなかったため、「近くない」として返しました（以降は出しません）。");
            }
            __result = false;
            return null;
        }

        /// <summary>
        /// 背骨が破棄されていれば GetBones で探し直す。探し直せた・破棄されていなければ true。
        /// 探し直してもボーンが揃わないとき、またはキャラが無いときは false（原型を飛ばす）。
        /// </summary>
        private static bool EnsureBones(MonoBehaviour bc)
        {
            if (!(bool)_isInit.GetValue(bc)) return true;   // 初期化前は原型がすぐ抜ける
            var cha = _cha.GetValue(bc, null) as ChaControl;
            if (cha == null) return false;                  // 原型は cha を確かめずに使う

            object spine = _spine.GetValue(bc, null);
            bool stale = false;
            if (spine != null)
            {
                foreach (FieldInfo f in _probeFields)
                {
                    object offsets = f.GetValue(spine);
                    if (offsets != null && IsDestroyed(_offsetsTrs.GetValue(offsets))) { stale = true; break; }
                }
            }
            if (!stale) return true;

            int id = bc.GetInstanceID();
            if (_retryFrame.TryGetValue(id, out int next) && Time.frameCount < next) return false;

            // 探し直す前の、部位ごとのボーン（破棄済みの参照）
            var oldParts = new object[_parts.Count];
            var oldBones = new Dictionary<string, object>();
            for (int i = 0; i < _parts.Count; i++)
            {
                oldParts[i] = _parts[i].Key.GetValue(bc, null);
                CollectBones(_parts[i], oldParts[i], oldBones);
            }

            _getBones.Invoke(null, new object[] { cha, bc });

            var newBones = new Dictionary<string, object>();
            for (int i = 0; i < _parts.Count; i++) CollectBones(_parts[i], _parts[i].Key.GetValue(bc, null), newBones);
            var missing = new List<string>();
            foreach (KeyValuePair<string, object> old in oldBones)
            {
                if (old.Value == null) continue; // 初めから無かったボーン
                newBones.TryGetValue(old.Key, out object t);
                if ((t as Transform) == null) missing.Add(old.Key);
            }
            if (missing.Count > 0)
            {
                // まだ揃っていない。元に戻して後で探し直す
                for (int i = 0; i < _parts.Count; i++) _parts[i].Key.SetValue(bc, oldParts[i], null);
                _retryFrame[id] = Time.frameCount + RetryIntervalFrames;
                if (_missingLogged.Add(id))
                    PatcherPlugin.Logger.LogInfo($"{Tag} {cha.name} のボーンが破棄され、まだ揃っていません（{string.Join("・", missing.ToArray())} など）。揃うまで CtrlBones の処理を止めます（{RetryIntervalFrames}フレームおきに探します）。");
                return false;
            }
            _retryFrame.Remove(id);
            _missingLogged.Remove(id);

            // 補正の一覧の Transform を、同じボーンの新しい Transform に付け替える
            var remap = new Dictionary<object, Transform>(RefComparer<object>.Instance);
            foreach (KeyValuePair<string, object> old in oldBones)
            {
                if (old.Value != null && newBones.TryGetValue(old.Key, out object t) && !remap.ContainsKey(old.Value)) remap[old.Value] = t as Transform;
            }
            int remapped = RemapOffsets(bc, remap);
            PatcherPlugin.Logger.LogInfo($"{Tag} {cha.name} の破棄されたボーンを探し直しました（{newBones.Count}本。補正の付け替え {remapped}件）。");
            return true;
        }

        /// <summary>部位の Offsets の trs を「部位.フィールド名」をキーに集める（Transform は破棄済みでも参照のまま入れる）。</summary>
        private static void CollectBones(KeyValuePair<PropertyInfo, FieldInfo[]> part, object partObj, Dictionary<string, object> into)
        {
            if (partObj == null) return;
            foreach (FieldInfo f in part.Value)
            {
                object offsets = f.GetValue(partObj);
                into[part.Key.Name + "." + f.Name] = offsets != null ? _offsetsTrs.GetValue(offsets) : null;
            }
        }

        /// <summary>補正の一覧の破棄された Transform を付け替える。付け替えた件数を返す。</summary>
        private static int RemapOffsets(MonoBehaviour bc, Dictionary<object, Transform> remap)
        {
            int count = 0;
            foreach (FieldInfo listField in AllOffsetLists())
            {
                var list = listField.GetValue(bc) as IList;
                if (list == null) continue;
                FieldInfo trsField = listField == _advOffsetList ? _advOffsetsTrs : _offsetsTrs;
                foreach (object item in list)
                {
                    if (item == null) continue;
                    object trs = trsField.GetValue(item);
                    if (trs != null && (trs as Transform) == null && remap.TryGetValue(trs, out Transform t) && t != null)
                    {
                        trsField.SetValue(item, t);
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>補正の一覧から、Transform が無い（破棄済み・初めから null）ものを取り除く。原型はこれに触ると例外で止まる。</summary>
        private static void RemoveStaleOffsets(MonoBehaviour bc)
        {
            foreach (FieldInfo listField in AllOffsetLists())
            {
                var list = listField.GetValue(bc) as IList;
                if (list == null || list.Count == 0) continue;
                FieldInfo trsField = listField == _advOffsetList ? _advOffsetsTrs : _offsetsTrs;
                int removed = 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object item = list[i];
                    if (item != null && (trsField.GetValue(item) as Transform) != null) continue;
                    list.RemoveAt(i);
                    removed++;
                }
                if (removed > 0)
                {
                    var cha = _cha.GetValue(bc, null) as ChaControl;
                    PatcherPlugin.Logger.LogInfo($"{Tag} {(cha != null ? cha.name : "?")} の補正（{listField.Name}）のうち、ボーンが無い {removed}件を取り除きました（残り {list.Count}件）。");
                }
            }
        }

        private static IEnumerable<FieldInfo> AllOffsetLists()
        {
            foreach (FieldInfo f in _offsetLists) yield return f;
            yield return _advOffsetList;
        }

        /// <summary>参照はあるが Unity 上は破棄済み（== null）なら true。初めから null の参照は false。</summary>
        private static bool IsDestroyed(object raw) => raw != null && (raw as Transform) == null;

        private static readonly HashSet<string> _loggedExceptions = new HashSet<string>();

        private static void LogException(string where, Exception ex)
        {
            try
            {
                if (!_loggedExceptions.Add(where + ":" + ex.GetType().Name)) return; // 毎フレーム呼ばれるので、同じ種類は1回だけ
                PatcherPlugin.Logger.LogWarning($"{Tag}[{where}] 処理中に例外が起きたため、原型の処理に委ねます（以降、同じ種類の例外は出しません）: {ex}");
            }
            catch { }
        }

        /// <summary>
        /// 参照の同一性で比べる。UnityEngine.Object の Equals は破棄済みどうしを等しいとみなすため、破棄済みの Transform をキーにするときに使う。
        /// </summary>
        private sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly RefComparer<T> Instance = new RefComparer<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
