using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// ksPlug（通常版）の H の状態が、設定画面の「タイトルへ」で戻らない問題の回避策（docs/KSPLUG_ISSUES.md §10）。
    ///
    /// ksPlug は H の終わりを、<c>HSceneProc.EndProc</c> の Prefix（<c>Core.HSceneProc_EndProc</c> → <c>HManage.EndH</c> → <c>Dealer.SetContext(Default)</c>）と、
    /// <c>Dealer.Update</c> の <c>HFlag.isHSceneEnd</c> の確認だけで知る。設定画面の「タイトルへ」（<c>Utils.Scene.ReturnTitle</c>）はシーンを読み替えるだけで
    /// どちらも通らないため、<c>Dealer.Ctx</c> が H のまま残る。すると:
    /// - DontDestroyOnLoad の Dealer に付いた <c>HCtrl</c> が破棄されず、タイトル・ストーリーで毎フレーム例外を出す。次の H では2つ目が足される。
    /// - 招待キャラ（Dealer の子の GuestRoot。<c>DestroyAfterH</c> が <c>Ctx != H</c> を待って自分を破棄する）が破棄されず、ストーリーや次の H に残る。
    /// - <c>Dealer.ChaCoresInH</c>（Ctx が H 以外のときだけ空にする）に前の H の要素が残り、次の H の <c>EndH</c> が例外になって H を終えられない
    ///   （<c>EndProc</c> の Prefix が例外を投げるので、ゲームの <c>EndProc</c> も毎フレーム途中で止まる）。
    ///
    /// 回避策:
    /// - A. タイトルのシーンを読んだとき、Ctx が H・Esthetic・EstResult のままで HSceneProc が無ければ、ksPlug 自身の <c>Dealer.SetContext(Default)</c> を呼ぶ。
    ///   HCtrl の破棄・招待キャラの破棄（DestroyAfterH）・ChaCoresInH の片付けは ksPlug の通常の終わりと同じ経路で行われる。
    ///   EndH は呼ばない（HFlag が既に無い。タイトルに戻った時点でゲームも H の結果を捨てている）。Dealer に残った2つ目以降の HCtrl も破棄する。
    /// - B. <c>HManage.EndH</c> の前に ChaCoresInH から null・破棄済みを取り除く。それでも <c>Core.HSceneProc_EndProc</c> が例外になったら、
    ///   例外を止めて Ctx を Default に戻す（ゲームの EndProc が続けて実行され、H を終えられる）。
    ///
    /// ksPlugVR は VRTitle を読んだときに自分で Ctx を Title に戻す（その時に HCtrl・ChaCoresInH も片付ける）ので対象外。
    /// </summary>
    internal static class HContextResetOnTitle
    {
        private const string Tag = "[HContextReset]";

        private static Type _dealerType, _contextType, _hCtrlType, _aiGuestType;
        private static FieldInfo _coreDealer;              // ksPlug.Core.dealer（static）
        private static PropertyInfo _dealerCtx;            // Dealer.Ctx
        private static PropertyInfo _dealerHCtrl;          // Dealer.HCtrl
        private static FieldInfo _dealerChaCoresInH;       // Dealer.ChaCoresInH（List<ChaCore>）
        private static MethodInfo _dealerSetContext;       // Dealer.SetContext(Context)
        private static object _ctxDefault;
        private static readonly HashSet<string> _staleCtxNames = new HashSet<string> { "H", "Esthetic", "EstResult" };

        private static int _guardRemovedTotal, _endProcRecovered;

        internal static void TryApply(Harmony harmony, Assembly asm, string root)
        {
            try
            {
                if (root != "ksPlug")
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {root} はタイトルで自分で H の状態を戻すため、タイトルでの H の状態の戻しは適用しません。");
                    return;
                }
                Type core = asm.GetType(root + ".Core");
                _dealerType = asm.GetType(root + ".Dealer");
                _contextType = _dealerType != null ? _dealerType.GetNestedType("Context", BindingFlags.Public | BindingFlags.NonPublic) : null;
                _hCtrlType = asm.GetType(root + ".HSession.HCtrl");
                _aiGuestType = asm.GetType(root + ".Character.AIGuest");
                _coreDealer = core != null ? AccessTools.Field(core, "dealer") : null;
                _dealerCtx = _dealerType != null ? AccessTools.Property(_dealerType, "Ctx") : null;
                _dealerHCtrl = _dealerType != null ? AccessTools.Property(_dealerType, "HCtrl") : null;
                _dealerChaCoresInH = _dealerType != null ? AccessTools.Field(_dealerType, "ChaCoresInH") : null;
                _dealerSetContext = _dealerType != null && _contextType != null ? AccessTools.Method(_dealerType, "SetContext", new[] { _contextType }) : null;
                if (_contextType != null && _contextType.IsEnum && Array.IndexOf(Enum.GetNames(_contextType), "Default") >= 0)
                    _ctxDefault = Enum.Parse(_contextType, "Default");

                if (_coreDealer == null || _dealerCtx == null || _dealerHCtrl == null || _dealerSetContext == null || _ctxDefault == null || _hCtrlType == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {root}.Core.dealer / Dealer.Ctx・HCtrl・SetContext(Context.Default) / HSession.HCtrl が見つからないため、" +
                                                    "タイトルでの H の状態の戻しを適用しません（ksPlug のバージョン違いの可能性）。");
                }
                else
                {
                    SceneManager.sceneLoaded += OnSceneLoaded;
                    PatcherPlugin.Logger.LogInfo($"{Tag} タイトルのシーンを読んだときに、{root} の H の状態（Dealer.Ctx）が残っていれば戻す回避策を適用しました。");
                }

                TryApplyEndHGuard(harmony, asm, root, core);
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        // ===================== A. タイトルで戻す =====================

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single || scene.name != "Title") return;
            if (!PatcherPlugin.EnableHContextResetOnTitle.Value || PatcherPlugin.IsQuitting) return;
            try
            {
                var dealer = _coreDealer.GetValue(null) as Component;
                if (dealer == null) return;
                object ctx = _dealerCtx.GetValue(dealer, null);
                string ctxName = Convert.ToString(ctx);
                if (!_staleCtxNames.Contains(ctxName)) return;
                if (UnityEngine.Object.FindObjectOfType<HSceneProc>() != null) return; // H がまだ動いている（ここには来ない見込み）

                Component[] hCtrls = dealer.gameObject.GetComponents(_hCtrlType);
                int guests = CountGuests();
                _dealerSetContext.Invoke(dealer, new[] { _ctxDefault });
                // SetContext は Dealer.HCtrl の1つだけを破棄する。H を重ねた分の古い HCtrl も破棄する
                int extra = 0;
                foreach (Component c in hCtrls)
                {
                    if (c == null) continue;
                    UnityEngine.Object.Destroy(c);
                    extra++;
                }
                PatcherPlugin.Logger.LogInfo($"{Tag} タイトルに戻ったとき ksPlug の H の状態が {ctxName} のまま残っていたため、Default に戻しました" +
                                             $"（ksPlug の H の制御 {hCtrls.Length}個を破棄、招待キャラ {guests}人は ksPlug が H の終わりと同じく破棄します）。");
                PatcherPlugin.Instance.StartCoroutine(CheckGuestsLater(guests));
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} タイトルでの H の状態の戻しに失敗しました: {ex}");
            }
        }

        private static int CountGuests()
        {
            if (_aiGuestType == null) return -1;
            UnityEngine.Object[] guests = UnityEngine.Object.FindObjectsOfType(_aiGuestType);
            return guests != null ? guests.Length : 0;
        }

        /// <summary>数秒後に、招待キャラと HCtrl が残っていないかを確かめる（ksPlug の DestroyAfterH は数フレームかけて破棄する）。</summary>
        private static IEnumerator CheckGuestsLater(int before)
        {
            yield return new WaitForSecondsRealtime(5f);
            int guests = CountGuests();
            var dealer = _coreDealer.GetValue(null) as Component;
            int hCtrls = dealer != null ? dealer.gameObject.GetComponents(_hCtrlType).Length : 0;
            if (guests > 0 || hCtrls > 0)
                PatcherPlugin.Logger.LogWarning($"{Tag} H の状態を戻した5秒後も、招待キャラ {guests}人（戻す前 {before}人）・ksPlug の H の制御 {hCtrls}個が残っています。");
            else
                PatcherPlugin.Logger.LogInfo($"{Tag} H の状態を戻した5秒後: 招待キャラ・ksPlug の H の制御は残っていません（戻す前の招待キャラ {before}人）。");
        }

        // ===================== B. EndH の保険 =====================

        private static void TryApplyEndHGuard(Harmony harmony, Assembly asm, string root, Type core)
        {
            Type hManage = asm.GetType(root + ".HSession.HManage");
            MethodInfo endH = hManage != null ? AccessTools.Method(hManage, "EndH", Type.EmptyTypes) : null;
            MethodInfo endProcPrefix = core != null ? AccessTools.Method(core, "HSceneProc_EndProc", Type.EmptyTypes) : null;
            if (endH == null || _dealerChaCoresInH == null || _coreDealer == null)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} {root}.HSession.HManage.EndH / Dealer.ChaCoresInH が見つからないため、H の終わりの保険を適用しません（ksPlug のバージョン違いの可能性）。");
                return;
            }
            harmony.Patch(endH, prefix: new HarmonyMethod(AccessTools.Method(typeof(HContextResetOnTitle), nameof(EndHPrefix))));
            string extra = "";
            if (endProcPrefix != null && _dealerCtx != null && _dealerSetContext != null && _ctxDefault != null)
            {
                harmony.Patch(endProcPrefix, finalizer: new HarmonyMethod(AccessTools.Method(typeof(HContextResetOnTitle), nameof(EndProcPrefixFinalizer))));
                extra = "、Core.HSceneProc_EndProc が例外になったときの H の終わり";
            }
            PatcherPlugin.Logger.LogInfo($"{Tag} H の終わりの保険（HManage.EndH の前の ChaCoresInH の片付け{extra}）を適用しました。");
        }

        /// <summary>ChaCoresInH から null・破棄済み（前の H の残り）を取り除く。原型は要素を確かめずに使う。</summary>
        private static void EndHPrefix()
        {
            if (!PatcherPlugin.EnableEndHGuard.Value) return;
            try
            {
                var dealer = _coreDealer.GetValue(null) as Component;
                if (dealer == null) return;
                var list = _dealerChaCoresInH.GetValue(dealer) as IList;
                if (list == null) return;
                int removed = 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var c = list[i] as UnityEngine.Object;
                    if (c != null) continue; // Unity の == で破棄済みも null
                    list.RemoveAt(i);
                    removed++;
                }
                if (removed > 0)
                {
                    _guardRemovedTotal += removed;
                    PatcherPlugin.Logger.LogInfo($"{Tag} H の終わり（HManage.EndH）の前に、ksPlug のキャラの一覧から前の H の残り（null・破棄済み）を {removed}件取り除きました（残り {list.Count}件、累計 {_guardRemovedTotal}件）。");
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} HManage.EndH の前の片付けに失敗しました（原型に委ねます）: {ex.Message}");
            }
        }

        /// <summary>
        /// ksPlug の EndProc の Prefix（EndH を含む）が例外になったら、例外を止めて Ctx を Default に戻す。
        /// 例外のままだとゲームの EndProc が毎フレーム途中で止まり、H を終えられない。
        /// </summary>
        private static Exception EndProcPrefixFinalizer(Exception __exception)
        {
            if (__exception == null || !PatcherPlugin.EnableEndHGuard.Value) return __exception;
            try
            {
                var dealer = _coreDealer.GetValue(null) as Component;
                string ctxName = dealer != null ? Convert.ToString(_dealerCtx.GetValue(dealer, null)) : "?";
                if (dealer != null && ctxName != "Default") _dealerSetContext.Invoke(dealer, new[] { _ctxDefault });
                _endProcRecovered++;
                if (_endProcRecovered <= 3)
                    PatcherPlugin.Logger.LogWarning($"{Tag} ksPlug の H の終わりの処理（Core.HSceneProc_EndProc）が例外になったため、例外を止めて H の状態を {ctxName} → Default に戻しました" +
                                                    $"（ゲームの H の終わりはそのまま続けます。{_endProcRecovered}回目）: {__exception}");
                return null;
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} H の終わりの例外の後始末に失敗しました（元の例外を出します）: {ex.Message}");
                return __exception;
            }
        }
    }
}
