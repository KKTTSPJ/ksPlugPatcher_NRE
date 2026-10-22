using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// ksPlugVR のメニュー（VRUI.CreateVRmenu で作るワールド空間のキャンバス）が、レイが当たっているのに他のキャンバスに取られてクリックできない問題の回避策（0.9.0、VR のみ）。
    /// 背景: docs/KSPLUG_ISSUES.md §12
    ///
    /// 【問題】
    ///   タイトル画面に限り、ksPlug のメニューがタイトルの UI のキャンバスの範囲らしき矩形に重なっていると、レイの色が変わり、ksPlug のメニューをクリックできない。
    ///
    /// 【原因（0.9.1。0.9.0 の LogRaycastHits のログで確認）】
    ///   VRTK の <c>VRTK_VRInputModule.CheckRaycasts</c> は、レイの元と向きを <c>pointerEventData.pointerCurrentRaycast</c> の worldPosition / worldNormal に入れて
    ///   <c>EventSystem.RaycastAll</c> を呼び、各キャンバスの <c>VRTK_UIGraphicRaycaster.Raycast</c> はそこからレイを作る。
    ///   ところが Raycast は、当たりがあると <c>SetNearestRaycast</c> で pointerCurrentRaycast をその当たり（worldPosition = 当たった点、worldNormal = 0）で上書きする。
    ///   そのため、先に調べたキャンバスに当たると、後に調べるキャンバスは「当たった点から向き 0」のレイになり、必ず外れる。
    ///   タイトルのキャンバス（VRTK_UICanvas がキャンバス全体に透明な板 VRTK_UICANVAS_DRAGGABLE_PANEL を置く）は ksPlug のメニューより先に登録されているので、
    ///   その範囲では ksPlug のメニューが手前にあっても当たらない。ログでも、ksPlug のメニューとタイトルのキャンバスが同時に当たったことは一度も無かった。
    ///   0.9.0 で見込んだ「sortingOrder が距離より先に比べられる」は、どちらも sortingOrder 0 で、原因ではなかった。
    ///
    /// 【処理】
    ///   1. （0.9.1、FixRayForKsPlugMenus）CheckRaycasts の中で ksPlug のメニューのキャンバスを調べるときだけ、pointerCurrentRaycast を元のレイ（最初のキャンバスが受け取った元と向き）に戻す。
    ///      ゲームのキャンバスどうしの調べ方は元のまま。結果は EventSystem が sortingOrder（どちらも 0）→ 距離の順に並べるので、手前にある方が先になる。
    ///   2. （0.9.0、PreferKsPlugMenu）結果の中に ksPlug のメニューの要素があり、先頭が ksPlug 以外のときだけ、ksPlug の要素を（順番を保って）先頭に移す。
    ///      既定（WhenNearer）では、ksPlug の要素の方がレイの元から近いときだけ移す。
    ///   ksPlug のメニューが当たっていないとき・奥にあるときは何もしないので、ゲームの UI の操作は変わらない。
    /// </summary>
    internal static class VRMenuRaycastPriority
    {
        private const string Tag = "[VRMenuRaycast]";
        private const int MaxLogsPerScene = 80;

        internal enum PreferMode { Off, WhenNearer, Always }

        private static FieldInfo _menuGo;
        /// <summary>ksPlugVR が作ったメニューのキャンバスの InstanceID。</summary>
        private static readonly HashSet<int> _ksPlugCanvasIds = new HashSet<int>();
        /// <summary>要素のいちばん近いキャンバスの InstanceID → ksPlug のメニューの中か。</summary>
        private static readonly Dictionary<int, bool> _isKsPlugCache = new Dictionary<int, bool>();
        private static readonly List<RaycastResult> _ks = new List<RaycastResult>();
        private static readonly List<RaycastResult> _others = new List<RaycastResult>();

        private static string _lastKey;
        private static string _logScene;
        private static int _logCount;
        private static int _moved;
        private static readonly HashSet<string> _loggedOnce = new HashSet<string>();

        internal static void TryApply(Harmony harmony)
        {
            if (!Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                Type module = AccessTools.TypeByName("VRTK.VRTK_VRInputModule");
                MethodInfo check = module != null ? AccessTools.Method(module, "CheckRaycasts") : null;
                Type raycaster = AccessTools.TypeByName("VRTK.VRTK_UIGraphicRaycaster");
                MethodInfo raycast = raycaster != null ? AccessTools.Method(raycaster, "Raycast", new[] { typeof(PointerEventData), typeof(List<RaycastResult>) }) : null;
                Assembly asm = PatcherPlugin.FindKsPlugAssembly(out string root);
                Type vrui = asm != null ? asm.GetType(root + ".Menu.VRUI") : null;
                Type frame = asm != null ? asm.GetType(root + ".Menu.VRUI+MenuFrame") : null;
                MethodInfo create = vrui != null ? AccessTools.Method(vrui, "CreateVRmenu") : null;
                _menuGo = frame != null ? AccessTools.Field(frame, "menuGo") : null;
                if (check == null || check.ReturnType != typeof(List<RaycastResult>) || create == null || _menuGo == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} VRTK_VRInputModule.CheckRaycasts（{check != null}）/ {root}.Menu.VRUI.CreateVRmenu（{create != null}）/ MenuFrame.menuGo（{_menuGo != null}）が見つからないため、" +
                        "ksPlug のメニューのレイの優先は行いません。");
                    return;
                }
                harmony.Patch(create, postfix: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRaycastPriority), nameof(CreateVRmenuPostfix))));
                harmony.Patch(check,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRaycastPriority), nameof(CheckRaycastsPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRaycastPriority), nameof(CheckRaycastsPostfix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRaycastPriority), nameof(CheckRaycastsFinalizer))));
                if (raycast != null)
                    harmony.Patch(raycast, prefix: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRaycastPriority), nameof(RaycastPrefix))));
                else
                    PatcherPlugin.Logger.LogWarning($"{Tag} VRTK_UIGraphicRaycaster.Raycast が見つからないため、ksPlug のメニューのレイを元に戻す処理は行いません（並べ替えだけ行います）。");
                PatcherPlugin.Logger.LogInfo($"{Tag} VRTK_VRInputModule.CheckRaycasts・VRTK_UIGraphicRaycaster.Raycast（{raycast != null}）と {root}.Menu.VRUI.CreateVRmenu に適用しました（{State()}）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        internal static string State() =>
            $"ksPlug のメニューのレイを元に戻す={(PatcherPlugin.FixVRMenuRay.Value ? "有効" : "無効")} / ksPlug のメニューを優先={PatcherPlugin.VRMenuRaycastMode.Value} / " +
            $"当たり先のログ={(PatcherPlugin.LogVRMenuRaycastHits.Value ? "有効" : "無効")}";

        // ===================== 1. ksPlug のメニューのレイを元に戻す（0.9.1） =====================

        /// <summary>今の CheckRaycasts の pointerEventData と、最初のキャンバスが受け取った元のレイ。</summary>
        private static PointerEventData _checking;
        private static bool _captured;
        private static Vector3 _origin, _direction;
        private static int _restoredThisCall, _restoredTotal;

        private static void CheckRaycastsPrefix()
        {
            _checking = null;
            _captured = false;
            _restoredThisCall = 0;
        }

        private static Exception CheckRaycastsFinalizer(Exception __exception)
        {
            _checking = null;
            _captured = false;
            return __exception;
        }

        private static void RaycastPrefix(Component __instance, PointerEventData eventData)
        {
            try
            {
                if (eventData == null || !PatcherPlugin.FixVRMenuRay.Value) return;
                if (!_captured)
                {
                    // CheckRaycasts の中の最初のキャンバス: まだ上書きされていない元のレイを覚える
                    _checking = eventData;
                    _origin = eventData.pointerCurrentRaycast.worldPosition;
                    _direction = eventData.pointerCurrentRaycast.worldNormal;
                    _captured = true;
                    return;
                }
                if (!ReferenceEquals(eventData, _checking) || __instance == null || !IsKsPlug(__instance.gameObject)) return;
                RaycastResult r = eventData.pointerCurrentRaycast;
                if (r.worldPosition == _origin && r.worldNormal == _direction) return; // 上書きされていない
                eventData.pointerCurrentRaycast = new RaycastResult { worldPosition = _origin, worldNormal = _direction };
                _restoredThisCall++;
                _restoredTotal++;
            }
            catch (Exception ex)
            {
                if (_loggedOnce.Add("restore:" + ex.GetType().Name))
                    PatcherPlugin.Logger.LogWarning($"{Tag} レイを元に戻す処理で例外が起きました（戻さずに続けます。同じ種類は以降出しません）: {ex}");
            }
        }

        // ===================== 2. ksPlug のメニューを先頭に（0.9.0） =====================

        private static void CreateVRmenuPostfix(object __result)
        {
            try
            {
                var go = __result != null ? _menuGo.GetValue(__result) as GameObject : null;
                var canvas = go != null ? go.GetComponent<Canvas>() : null;
                if (canvas == null) return;
                _ksPlugCanvasIds.Add(canvas.GetInstanceID());
                _isKsPlugCache.Clear(); // 新しいメニューの中の入れ子のキャンバスを判定し直す
            }
            catch { }
        }

        private static bool IsKsPlug(GameObject go)
        {
            if (go == null) return false;
            Canvas nearest = go.GetComponentInParent<Canvas>();
            if (nearest == null) return false;
            int id = nearest.GetInstanceID();
            if (_isKsPlugCache.TryGetValue(id, out bool v)) return v;
            v = false;
            for (Transform t = nearest.transform; t != null; t = t.parent)
            {
                Canvas c = t.GetComponent<Canvas>();
                if (c != null && _ksPlugCanvasIds.Contains(c.GetInstanceID())) { v = true; break; }
            }
            if (_isKsPlugCache.Count > 4096) _isKsPlugCache.Clear();
            _isKsPlugCache[id] = v;
            return v;
        }

        private static void CheckRaycastsPostfix(List<RaycastResult> __result)
        {
            try
            {
                if (__result == null) return;
                bool log = PatcherPlugin.LogVRMenuRaycastHits.Value;
                PreferMode mode = PatcherPlugin.VRMenuRaycastMode.Value;
                if (!log && mode == PreferMode.Off) return; // 既定（0.10.2）は並べ替えもログも無いので、当たり先を調べない
                string before = log ? Describe(__result) : null;

                int firstKs = -1;
                for (int i = 0; i < __result.Count; i++)
                {
                    if (IsKsPlug(__result[i].gameObject)) { firstKs = i; break; }
                }
                bool moved = false;
                if (mode != PreferMode.Off && firstKs > 0)
                {
                    bool nearer = __result[firstKs].distance <= __result[0].distance + 1e-4f;
                    if (mode == PreferMode.Always || nearer)
                    {
                        _ks.Clear();
                        _others.Clear();
                        for (int i = 0; i < __result.Count; i++)
                        {
                            if (IsKsPlug(__result[i].gameObject)) _ks.Add(__result[i]);
                            else _others.Add(__result[i]);
                        }
                        __result.Clear();
                        __result.AddRange(_ks);
                        __result.AddRange(_others);
                        moved = true;
                        _moved++;
                    }
                }
                if (log) LogIfChanged(before, __result, moved);
            }
            catch (Exception ex)
            {
                if (_loggedOnce.Add(ex.GetType().Name))
                    PatcherPlugin.Logger.LogWarning($"{Tag} レイの当たり先の並べ替えで例外が起きました（並べ替えずに続けます。同じ種類は以降出しません）: {ex}");
            }
        }

        /// <summary>当たり先の先頭が変わったときだけ、上位4つ（キャンバス・sortingLayer・sortingOrder・距離）を出す。シーンごとに上限まで。</summary>
        private static void LogIfChanged(string before, List<RaycastResult> after, bool moved)
        {
            string key = (after.Count > 0 && after[0].gameObject != null ? after[0].gameObject.GetInstanceID().ToString() : "-") + (moved ? "m" : "");
            if (key == _lastKey) return;
            _lastKey = key;
            if (after.Count == 0) return; // 何にも当たらなくなったときは出さない（次に当たったときに出る）

            string scene = SceneManager.GetActiveScene().name;
            if (scene != _logScene) { _logScene = scene; _logCount = 0; }
            if (_logCount >= MaxLogsPerScene) return;
            _logCount++;
            PatcherPlugin.Logger.LogInfo($"{Tag} レイの当たり先（シーン={scene}、{_logCount}/{MaxLogsPerScene}）: {before}" +
                (_restoredThisCall > 0 ? $" ／ ksPlug のメニューのレイを元に戻しました（{_restoredThisCall}個、累計 {_restoredTotal}回）" : "") +
                (moved ? $" → ksPlug のメニューを先頭に移しました（累計 {_moved}回）" : ""));
        }

        private static string Describe(List<RaycastResult> results)
        {
            if (results.Count == 0) return "なし";
            var sb = new StringBuilder();
            int n = Math.Min(4, results.Count);
            for (int i = 0; i < n; i++)
            {
                RaycastResult r = results[i];
                if (i > 0) sb.Append(" ／ ");
                GameObject go = r.gameObject;
                Canvas c = go != null ? go.GetComponentInParent<Canvas>() : null;
                Canvas root = c != null ? c.rootCanvas : null;
                sb.Append($"{i + 1}. {(go != null ? go.name : "?")}（キャンバス {(root != null ? root.name : "?")}" +
                    $"・layer {SortingLayer.IDToName(r.sortingLayer)}・order {r.sortingOrder}・距離 {r.distance:F3}" +
                    $"{(IsKsPlug(go) ? "・ksPlug" : "")}）");
            }
            if (results.Count > n) sb.Append($" ほか {results.Count - n}個");
            return sb.ToString();
        }
    }
}
