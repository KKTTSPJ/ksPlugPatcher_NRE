using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// ksPlug / ksPlugVR が、破棄されたオブジェクトを持ち続けて毎フレーム NullReferenceException を出す問題の回避策。
    /// KK の kPlugPatcher_InviteChrID の CoordinateChangeStaleRefFix（kPlug の ColDefsManager など）に当たる。
    ///
    /// 実機ログで、H の開始時の衣装の読み直し（CoordinateChanged）の直後から
    /// 最後まで、次の2つがそれぞれ 1804件（ほぼ毎フレーム）。ksPlugVR も同じ作り。
    ///
    /// - <c>Character.ChaTools+ColDefsManager.LateUpdate</c>: キャラに付けた当たり判定の表示用オブジェクト（gos）を、キャラの表示に合わせて毎フレーム SetActive する。
    ///   gos に足す処理（GetOrCreate）しかなく、破棄されたものを取り除かない。kPlug の ColDefsManager と同じ。
    ///   → 破棄済みを gos から取り除いてから原型を実行する（kPlugPatcher と同じ）。
    /// - <c>Character.BellyCtrl.LateUpdate</c>: お腹の膨らみ。初期化（IE_Init）で一度だけ探したボーン（cf_s_waist02 / cf_s_waist01 / cf_s_spine01 / cf_s_spine02）の
    ///   Transform を BoneTweak.t に持ち、毎フレーム LerpInflation で動かす。体が作り直されると破棄されたボーンに触って例外になり、
    ///   それより後の処理（お腹の膨らみの増加、設定の切り替えの反映）も止まる。
    ///   → 破棄されたボーンを、同じ名前のボーンからキャラの中で探し直す（ksPlug の GetBone と同じ探し方）。元の位置・回転・拡大（locPos など）は初期化のときの値のまま。
    ///   見つからないとき（体の読み込みの途中など）は、そのフレームの原型を飛ばす（原型を実行しても例外になるだけ）。
    /// </summary>
    internal static class KsPlugStaleRefFix
    {
        private const string Tag = "[ksPlugStaleRefFix]";

        // ColDefsManager
        private static FieldInfo _colDefsGos;          // List<GameObject>
        private static FieldInfo _colDefsRelatedCha;   // ChaControl

        // BellyCtrl
        private static FieldInfo _bellyCha;            // ChaControl
        private static FieldInfo _bellyIsInit;         // bool
        private static Type _boneTweakType;
        private static FieldInfo _boneT;               // Transform
        private static FieldInfo _boneLocPos, _boneLocRot, _boneLocScl;   // Vector3
        private static readonly List<KeyValuePair<FieldInfo, string>> _bellyBones = new List<KeyValuePair<FieldInfo, string>>();

        /// <summary>ボーンが見つからなかったインスタンスごとの、次に探してよいフレーム（毎フレーム全部の Transform を探さないため）。</summary>
        private static readonly Dictionary<int, int> _bellyRetryFrame = new Dictionary<int, int>();
        private const int BellyRetryIntervalFrames = 30;
        private static readonly HashSet<int> _bellyMissingLogged = new HashSet<int>();

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                Assembly asm = PatcherPlugin.FindKsPlugAssembly(out string root);
                if (asm == null)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {root} が見つからないため、ksPlug への回避策は適用しません（ksPlug を使っていなければ問題ありません）。");
                    return;
                }
                TryPatchColDefsManager(harmony, asm.GetType(root + ".Character.ChaTools+ColDefsManager"), root);
                TryPatchBellyCtrl(harmony, asm.GetType(root + ".Character.BellyCtrl"), root);
                CtrlBonesStaleRefFix.TryApply(harmony, asm, root);
                LazyInviteCardList.TryApply(harmony, asm, root);
                if (PatcherPlugin.EnableMaterialNullGuard.Value) MaterialNullGuard.TryApply(harmony, asm, root);
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        // ===================== ColDefsManager =====================

        private static void TryPatchColDefsManager(Harmony harmony, Type type, string root)
        {
            string label = root + ".Character.ChaTools+ColDefsManager";
            try
            {
                _colDefsGos = type != null ? AccessTools.Field(type, "gos") : null;
                _colDefsRelatedCha = type != null ? AccessTools.Field(type, "relatedCha") : null;
                MethodInfo lateUpdate = type != null ? AccessTools.Method(type, "LateUpdate", Type.EmptyTypes) : null;
                if (_colDefsGos == null || _colDefsGos.FieldType != typeof(List<GameObject>) || _colDefsRelatedCha == null || lateUpdate == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {label}（gos / relatedCha / LateUpdate）が見つからないため、回避策を適用しません（ksPlug のバージョン違いの可能性）。");
                    return;
                }
                harmony.Patch(lateUpdate, prefix: new HarmonyMethod(AccessTools.Method(typeof(KsPlugStaleRefFix), nameof(ColDefsManagerLateUpdatePrefix))));
                PatcherPlugin.Logger.LogInfo($"{Tag} {label}.LateUpdate に回避策を適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} {label} への適用に失敗しました: {ex.Message}");
            }
        }

        /// <summary>gos から破棄済み・null を取り除く。キャラ（relatedCha）が無ければ原型を飛ばす（原型は relatedCha を確かめずに使う）。</summary>
        private static bool ColDefsManagerLateUpdatePrefix(object __instance)
        {
            if (PatcherPlugin.IsQuitting || !PatcherPlugin.EnableColDefsManagerStaleRefFix.Value) return true;
            try
            {
                var gos = _colDefsGos.GetValue(__instance) as List<GameObject>;
                if (gos == null) return true;
                var cha = _colDefsRelatedCha.GetValue(__instance) as ChaControl;
                if (cha == null) return false;
                int removed = gos.RemoveAll(go => go == null); // Unity の == で破棄済みも取り除く
                if (removed > 0)
                    PatcherPlugin.Logger.LogInfo($"{Tag}[ColDefsManager] {cha.name} の破棄された当たり判定の表示用オブジェクトを {removed}件取り除きました（残り {gos.Count}件）。");
                return true;
            }
            catch (Exception ex)
            {
                LogException("ColDefsManager", ex);
                return true;
            }
        }

        // ===================== BellyCtrl =====================

        private static void TryPatchBellyCtrl(Harmony harmony, Type type, string root)
        {
            string label = root + ".Character.BellyCtrl";
            try
            {
                _boneTweakType = type != null ? type.GetNestedType("BoneTweak", BindingFlags.Public | BindingFlags.NonPublic) : null;
                _bellyCha = type != null ? AccessTools.Field(type, "cha") : null;
                _bellyIsInit = type != null ? AccessTools.Field(type, "isInit") : null;
                MethodInfo lateUpdate = type != null ? AccessTools.Method(type, "LateUpdate", Type.EmptyTypes) : null;
                if (_boneTweakType != null)
                {
                    _boneT = AccessTools.Field(_boneTweakType, "t");
                    _boneLocPos = AccessTools.Field(_boneTweakType, "locPos");
                    _boneLocRot = AccessTools.Field(_boneTweakType, "locRot");
                    _boneLocScl = AccessTools.Field(_boneTweakType, "locScl");
                }
                _bellyBones.Clear();
                if (type != null)
                {
                    // ksPlug の IE_Init と同じボーン名
                    AddBellyBone(type, "waist2", "cf_s_waist02");
                    AddBellyBone(type, "waist1", "cf_s_waist01");
                    AddBellyBone(type, "spine1", "cf_s_spine01");
                    AddBellyBone(type, "spine2", "cf_s_spine02");
                }
                if (_bellyCha == null || _bellyIsInit == null || lateUpdate == null || _boneT == null || _boneLocPos == null
                    || _boneLocRot == null || _boneLocScl == null || _bellyBones.Count != 4)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {label}（cha / isInit / waist2・waist1・spine1・spine2 / BoneTweak / LateUpdate）が見つからないため、回避策を適用しません（ksPlug のバージョン違いの可能性）。");
                    return;
                }
                harmony.Patch(lateUpdate, prefix: new HarmonyMethod(AccessTools.Method(typeof(KsPlugStaleRefFix), nameof(BellyCtrlLateUpdatePrefix))));
                PatcherPlugin.Logger.LogInfo($"{Tag} {label}.LateUpdate に回避策を適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} {label} への適用に失敗しました: {ex.Message}");
            }
        }

        private static void AddBellyBone(Type type, string field, string boneName)
        {
            FieldInfo f = AccessTools.Field(type, field);
            if (f != null && f.FieldType == _boneTweakType) _bellyBones.Add(new KeyValuePair<FieldInfo, string>(f, boneName));
        }

        /// <summary>
        /// 初期化済みで、ボーンが破棄されていたら探し直す。見つからなければそのフレームの原型を飛ばす。
        /// 初期化前（isInit=false）は原型がすぐ抜けるので何もしない。
        /// </summary>
        private static bool BellyCtrlLateUpdatePrefix(object __instance)
        {
            if (PatcherPlugin.IsQuitting || !PatcherPlugin.EnableBellyCtrlStaleRefFix.Value) return true;
            try
            {
                if (!(bool)_bellyIsInit.GetValue(__instance)) return true;
                var cha = _bellyCha.GetValue(__instance) as ChaControl;
                if (cha == null) return false; // 原型は cha を確かめずに使う

                List<KeyValuePair<FieldInfo, string>> broken = null;
                foreach (KeyValuePair<FieldInfo, string> bone in _bellyBones)
                {
                    object tweak = bone.Key.GetValue(__instance);
                    if (tweak != null && (_boneT.GetValue(tweak) as Transform) != null) continue;
                    if (broken == null) broken = new List<KeyValuePair<FieldInfo, string>>();
                    broken.Add(bone);
                }
                if (broken == null) return true;

                int id = cha.GetInstanceID();
                if (_bellyRetryFrame.TryGetValue(id, out int next) && Time.frameCount < next) return false;

                Dictionary<string, Transform> found = FindBones(cha, broken);
                var repaired = new List<string>();
                var missing = new List<string>();
                foreach (KeyValuePair<FieldInfo, string> bone in broken)
                {
                    if (!found.TryGetValue(bone.Value, out Transform t)) { missing.Add(bone.Value); continue; }
                    object tweak = bone.Key.GetValue(__instance);
                    if (tweak == null)
                    {
                        // ksPlug の GetBone は、初期化のときにボーンが無いと null を入れる。原型はそれも確かめずに使うので、GetBone と同じ値で作る
                        tweak = Activator.CreateInstance(_boneTweakType);
                        _boneLocPos.SetValue(tweak, t.localPosition);
                        _boneLocRot.SetValue(tweak, t.localEulerAngles);
                        _boneLocScl.SetValue(tweak, t.localScale);
                        bone.Key.SetValue(__instance, tweak);
                    }
                    _boneT.SetValue(tweak, t);
                    repaired.Add(bone.Value);
                }

                if (missing.Count > 0)
                {
                    _bellyRetryFrame[id] = Time.frameCount + BellyRetryIntervalFrames;
                    if (_bellyMissingLogged.Add(id)) // 見つからない旨は最初の1回だけ出す
                    {
                        PatcherPlugin.Logger.LogInfo($"{Tag}[BellyCtrl] {cha.name} のお腹のボーン {string.Join("・", missing.ToArray())} が破棄され、見つかりません。見つかるまでお腹の処理を止めます（{BellyRetryIntervalFrames}フレームおきに探します）。");
                    }
                    return false;
                }
                _bellyRetryFrame.Remove(id);
                _bellyMissingLogged.Remove(id);
                PatcherPlugin.Logger.LogInfo($"{Tag}[BellyCtrl] {cha.name} の破棄されたお腹のボーン {string.Join("・", repaired.ToArray())} を探し直しました。");
                return true;
            }
            catch (Exception ex)
            {
                LogException("BellyCtrl", ex);
                return true;
            }
        }

        /// <summary>キャラの中から、名前が一致するボーンを探す（ksPlug の ChaTools.FindInCha と同じく、最初に見つかったもの）。</summary>
        private static Dictionary<string, Transform> FindBones(ChaControl cha, List<KeyValuePair<FieldInfo, string>> bones)
        {
            var result = new Dictionary<string, Transform>();
            var names = new HashSet<string>();
            foreach (KeyValuePair<FieldInfo, string> bone in bones) names.Add(bone.Value);
            foreach (Transform t in cha.GetComponentsInChildren<Transform>())
            {
                if (t == null || !names.Contains(t.name) || result.ContainsKey(t.name)) continue;
                result[t.name] = t;
                if (result.Count == names.Count) break;
            }
            return result;
        }

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
    }
}
