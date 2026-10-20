using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// H で「ゲージが上がっていないのに絶頂が繰り返される（絶頂ループ）」の回避策（ゲーム本体 Assembly-CSharp へのパッチ）。
    /// KK の kPlugPatcher_InviteChrID の ParentObjectCtrlStaleRefFix の KKS 版。
    ///
    /// 【不具合の仕組み（通常版と VR 版は同じ）】
    ///   ParentObjectCtrl は H の小物の GameObject を dicObject に持ち、体位が変わるたびに表示を切り替える。
    ///   衣装の読み直しなどで小物が破棄されても dicObject に残り、次の4つが確かめずに SetActive して NullReferenceException を出す
    ///   （同じクラスの ReleaseObject と SetColPhase だけは null を確かめている）。
    ///   - Visible(bool) / Proc(string): KK と同じ。Visible は isActive の更新に届かないので、次も同じ条件で例外が続く。
    ///   - VisibleObi(bool) / ProcObi(string): KKS で増えた Obi（汁の物理）版。HActionBase.SetPlay は普段こちらを呼ぶ。
    ///     ProcObi は isAllVisible のとき lstObjTouch（触る用の小物）を、そうでないとき VisibleObi と dicInfo の小物を切り替える。
    ///
    /// 【絶頂ループにつながる経路】
    ///   HSceneProc.Update → HSonyu.Proc → HActionBase.SetPlay("WS_IN_A") → ParentObjectCtrl.ProcObi → VisibleObi で例外。
    ///   HSonyu.Proc の絶頂の終わりは SetPlay の後に flags.gaugeFemale = 0f があり、例外でそこに届かない。
    ///   ゲージが満タンのまま残り、次のフレームで絶頂の条件がまた成り立つ。
    ///   実機ログで ProcObi の例外 23件
    ///   （HSonyu.Proc 18件、HSonyu.LoopProc 4件、ksPlug の交代 AIGuest.IE_PullForSwap → HManage.PlayStateOnMains 1件）。
    ///
    /// 【回避策】
    ///   4つとも、破棄済み（Unity の null）の小物と、dicObject に無いキーを飛ばすだけの同じ処理に置き換える（Prefix で原型を飛ばす）。
    ///   Obi の当たり判定の切り替え（private の SetColPhase）は、原型と同じく SetActive した小物ごとに呼ぶ。
    ///   dicObject から破棄済みの要素を消すことはしない（原型が dicObject[key] で引くので、消すと KeyNotFoundException になる）。
    /// </summary>
    internal static class ParentObjectCtrlStaleRefFix
    {
        private const string Tag = "[ParentObjectCtrlFix]";

        private static FieldInfo _dicObjectField;      // Dictionary<string, GameObject>
        private static FieldInfo _dicObjectCtrlField;  // Dictionary<string, List<string>>
        private static FieldInfo _dicInfoField;        // Dictionary<string, Dictionary<string, bool>>
        private static FieldInfo _lstObjTouchField;    // List<GameObject>
        private static FieldInfo _isAllVisibleField;   // bool（private）
        private static Action<ParentObjectCtrl, string> _setColPhase;

        /// <summary>インスタンスと処理ごとに、前回ログに出した飛ばした件数。同じ内容を出し続けないため。</summary>
        private static readonly Dictionary<string, int> _lastSkipped = new Dictionary<string, int>();

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                Type type = typeof(ParentObjectCtrl);
                _dicObjectField = AccessTools.Field(type, "dicObject");
                _dicObjectCtrlField = AccessTools.Field(type, "dicObjectCtrl");
                _dicInfoField = AccessTools.Field(type, "dicInfo");
                _lstObjTouchField = AccessTools.Field(type, "lstObjTouch");
                _isAllVisibleField = AccessTools.Field(type, "isAllVisible");
                MethodInfo setColPhase = AccessTools.Method(type, "SetColPhase", new[] { typeof(string) });
                if (setColPhase != null)
                    _setColPhase = (Action<ParentObjectCtrl, string>)Delegate.CreateDelegate(typeof(Action<ParentObjectCtrl, string>), setColPhase);

                if (_dicObjectField == null || _dicObjectCtrlField == null || _dicInfoField == null || _lstObjTouchField == null
                    || _isAllVisibleField == null || _setColPhase == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} ParentObjectCtrl のフィールド・メソッド（dicObject / dicObjectCtrl / dicInfo / lstObjTouch / isAllVisible / SetColPhase）が見つからないため、回避策を適用しません（ゲームのバージョン違いの可能性）。");
                    return;
                }

                Patch(harmony, type, "Visible", nameof(VisiblePrefix));
                Patch(harmony, type, "VisibleObi", nameof(VisibleObiPrefix));
                Patch(harmony, type, "Proc", nameof(ProcPrefix));
                Patch(harmony, type, "ProcObi", nameof(ProcObiPrefix));
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        private static void Patch(Harmony harmony, Type type, string name, string prefix)
        {
            Type argType = name.StartsWith("Visible") ? typeof(bool) : typeof(string);
            MethodInfo target = AccessTools.Method(type, name, new[] { argType });
            if (target == null || target.ReturnType != typeof(bool))
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} ParentObjectCtrl.{name}({argType.Name}) が見つからないため、この処理には回避策を適用しません。");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ParentObjectCtrlStaleRefFix), prefix)));
            PatcherPlugin.Logger.LogInfo($"{Tag} ParentObjectCtrl.{name}({argType.Name}) に回避策を適用しました。");
        }

        private static bool Active => !PatcherPlugin.IsQuitting && PatcherPlugin.EnableParentObjectCtrlStaleRefFix.Value;

        // ---- 原型と同じ処理（破棄済みを飛ばす）----------------------------------------------------

        /// <summary>原型 Visible(bool)。isActive が同じなら何もしない。全部の小物を切り替えて isActive を更新する。戻り値は常に false。</summary>
        private static bool VisiblePrefix(ParentObjectCtrl __instance, bool _visible, ref bool __result)
        {
            if (!Active) return true;
            try
            {
                // ParentObjectCtrl 自体が破棄済みで、呼び出し元が古い参照を持っている場合（KK で実例あり）。原型に委ねると例外になる
                if (__instance == null) { __result = false; return false; }
                if (!SafeVisible(__instance, _visible)) return true;
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                LogException("Visible", ex);
                return true;
            }
        }

        /// <summary>原型 VisibleObi(bool)。isActive を見ずに全部の小物を切り替える（isActive は更新しない）。戻り値は常に false。</summary>
        private static bool VisibleObiPrefix(ParentObjectCtrl __instance, bool _visible, ref bool __result)
        {
            if (!Active) return true;
            try
            {
                if (__instance == null) { __result = false; return false; }
                var dicObject = _dicObjectField.GetValue(__instance) as Dictionary<string, GameObject>;
                if (dicObject == null) return true;
                int skipped = SetAll(__instance, dicObject, _visible);
                LogSkipped(__instance, "VisibleObi", skipped, $"visible={_visible}");
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                LogException("VisibleObi", ex);
                return true;
            }
        }

        /// <summary>
        /// 原型 Proc(string)。Visible(isAllVisible) の後、その体位の小物（dicObjectCtrl）を !isAllVisible にする。
        /// 体位が dicObjectCtrl に無ければ false、あれば isActive = !isAllVisible にして true。
        /// </summary>
        private static bool ProcPrefix(ParentObjectCtrl __instance, string _nameNextAnimation, ref bool __result)
        {
            if (!Active) return true;
            try
            {
                if (__instance == null) { __result = false; return false; }
                bool isAllVisible = (bool)_isAllVisibleField.GetValue(__instance);
                if (!SafeVisible(__instance, isAllVisible)) return true;

                var dicObjectCtrl = _dicObjectCtrlField.GetValue(__instance) as Dictionary<string, List<string>>;
                var dicObject = _dicObjectField.GetValue(__instance) as Dictionary<string, GameObject>;
                if (dicObjectCtrl == null || _nameNextAnimation == null || !dicObjectCtrl.TryGetValue(_nameNextAnimation, out List<string> keys))
                {
                    __result = false;
                    return false;
                }
                __instance.isActive = !isAllVisible;
                int skipped = 0;
                if (keys != null)
                {
                    foreach (string key in keys)
                    {
                        if (!TryGetAlive(dicObject, key, out GameObject go)) { skipped++; continue; }
                        go.SetActive(!isAllVisible);
                        _setColPhase(__instance, key);
                    }
                }
                LogSkipped(__instance, "Proc", skipped, $"anim={_nameNextAnimation}");
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                LogException("Proc", ex);
                return true;
            }
        }

        /// <summary>
        /// 原型 ProcObi(string)。
        /// isAllVisible なら、触る用の小物（lstObjTouch）のうち非表示のものを表示する。
        /// そうでなければ VisibleObi(false) の後、その体位の小物（dicInfo）をそれぞれの値にする。体位が dicInfo に無ければ false。
        /// </summary>
        private static bool ProcObiPrefix(ParentObjectCtrl __instance, string _nameNextAnimation, ref bool __result)
        {
            if (!Active) return true;
            try
            {
                if (__instance == null) { __result = false; return false; }
                bool isAllVisible = (bool)_isAllVisibleField.GetValue(__instance);
                int skipped = 0;
                if (isAllVisible)
                {
                    var lstObjTouch = _lstObjTouchField.GetValue(__instance) as List<GameObject>;
                    if (lstObjTouch == null) return true;
                    foreach (GameObject go in lstObjTouch)
                    {
                        if (go == null) { skipped++; continue; }
                        if (!go.activeSelf) go.SetActive(true);
                    }
                }
                else
                {
                    var dicObject = _dicObjectField.GetValue(__instance) as Dictionary<string, GameObject>;
                    var dicInfo = _dicInfoField.GetValue(__instance) as Dictionary<string, Dictionary<string, bool>>;
                    if (dicObject == null || dicInfo == null) return true;
                    skipped += SetAll(__instance, dicObject, false); // 原型の VisibleObi(false)
                    if (_nameNextAnimation == null || !dicInfo.TryGetValue(_nameNextAnimation, out Dictionary<string, bool> info))
                    {
                        LogSkipped(__instance, "ProcObi", skipped, $"anim={_nameNextAnimation}");
                        __result = false;
                        return false;
                    }
                    if (info != null)
                    {
                        foreach (KeyValuePair<string, bool> item in info)
                        {
                            if (!TryGetAlive(dicObject, item.Key, out GameObject go)) { skipped++; continue; }
                            go.SetActive(item.Value);
                            _setColPhase(__instance, item.Key);
                        }
                    }
                }
                LogSkipped(__instance, "ProcObi", skipped, $"anim={_nameNextAnimation}");
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                LogException("ProcObi", ex);
                return true;
            }
        }

        /// <summary>
        /// 原型 Visible(bool) と同じ処理。isActive が同じなら何もしない。破棄済みを飛ばして全部切り替え、isActive を更新する。
        /// dicObject が無いなど想定外のときは false（呼び出し側が原型に委ねる）。
        /// </summary>
        private static bool SafeVisible(ParentObjectCtrl instance, bool visible)
        {
            if (instance.isActive == visible) return true;
            var dicObject = _dicObjectField.GetValue(instance) as Dictionary<string, GameObject>;
            if (dicObject == null) return false;
            int skipped = SetAll(instance, dicObject, visible);
            // 原型はここに届かず isActive が変わらないため、同じ状態で例外が続いていた
            instance.isActive = visible;
            LogSkipped(instance, "Visible", skipped, $"visible={visible}");
            return true;
        }

        /// <summary>dicObject の全部の小物を <paramref name="visible"/> にし、Obi の当たり判定を合わせる。飛ばした件数を返す。</summary>
        private static int SetAll(ParentObjectCtrl instance, Dictionary<string, GameObject> dicObject, bool visible)
        {
            int skipped = 0;
            foreach (KeyValuePair<string, GameObject> item in dicObject)
            {
                if (item.Value == null) { skipped++; continue; } // Unity の == で破棄済みも弾く
                item.Value.SetActive(visible);
                _setColPhase(instance, item.Key);
            }
            return skipped;
        }

        private static bool TryGetAlive(Dictionary<string, GameObject> dicObject, string key, out GameObject go)
        {
            go = null;
            if (dicObject == null || key == null || !dicObject.TryGetValue(key, out go)) return false;
            return go != null;
        }

        /// <summary>飛ばした件数が前回と変わったときだけログに出す（同じ状態で毎回呼ばれるため）。</summary>
        private static void LogSkipped(ParentObjectCtrl instance, string where, int skipped, string detail)
        {
            try
            {
                string key = instance.GetInstanceID() + ":" + where;
                _lastSkipped.TryGetValue(key, out int last);
                if (last == skipped) return;
                _lastSkipped[key] = skipped;
                if (skipped > 0)
                    PatcherPlugin.Logger.LogInfo($"{Tag}[{where}] {instance.name} で、破棄済み・未登録の小物 {skipped}件を飛ばしました（{detail}）。");
            }
            catch { }
        }

        private static void LogException(string where, Exception ex)
        {
            try
            {
                PatcherPlugin.Logger.LogWarning($"{Tag}[{where}] 処理中に例外が起きたため、原型の処理に委ねます: {ex.Message}");
            }
            catch { }
        }
    }
}
