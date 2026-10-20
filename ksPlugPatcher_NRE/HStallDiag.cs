using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// H が絶頂の前後で先に進まなくなる問題を調べるための診断ログ（回避策ではない。既定は無効）。
    ///
    /// 3P の奉仕の OLoop（射精の前のループ）にとどまり、H の UI の操作が受け付けられず、終了ボタンも効かなくなる問題があった
    /// （原因の1つは HoushiVoiceWaitFix、docs/KSPLUG_ISSUES.md §11）。ゲームの OLoop は「中に出す／外に出す」のクリック（flags.click）を待つだけなので、
    /// クリックが届いているかを確かめるため、次をログに出す（通常版の HSceneProc のみ）。
    /// - flags.click が none 以外になったとき（ゲームのボタン・ksPlug の操作のどちらでも。H の1回あたり上限あり）。
    /// - OLoop・射精の後のループ（IN_Loop・OUT_Loop）・声の待ち（voiceWait）に StallSeconds 以上とどまったとき1回: 状態、rePlay、声の状態、
    ///   シーンの重なり（HSprite.IsSpriteAciotn の条件）、H のボタンの表示、ksPlug の操作を止める条件（メインの AI の判断中・キス中・UI の上）。
    /// - とどまっている間の左クリック（上限あり）: マウスの下の UI（EventSystem の Raycast の上位3つ）と、その時のシーンの重なり。
    /// - flags.voiceWait が変わったとき（H の UI が押せなくなる経路を調べるため。H の1回あたり上限あり。誰が変えたかは分からないので、状態・rePlay・click・フレームを出す）。
    /// </summary>
    internal static class HStallDiag
    {
        private const string Tag = "[HStallDiag]";
        private const int MaxClickLogsPerH = 200;
        private const int MaxStallClickLogs = 20;
        private const int MaxVoiceWaitLogsPerH = 300;

        private static FieldInfo _lstProc;                 // HSceneProc.lstProc（List<HActionBase>）

        // ksPlug（あれば）
        private static FieldInfo _coreDealer;
        private static PropertyInfo _dealerCtx, _dealerHCtrl, _dealerInProcess, _hCtrlCursorOverUIS;
        private static Type _hCtrlType;
        private static MethodInfo _mainAiInDecision, _areGuestInKiss;

        // 状態
        private static HSceneProc _proc;
        private static string _state;
        private static float _stateSince;
        private static bool _dumped;
        private static int _stallClickLogs, _clickLogs, _voiceWaitLogs;
        private static bool _lastVoiceWait;
        private static HFlag.ClickKind _lastClick = HFlag.ClickKind.none;
        private static string _lastClickText = "なし";
        private static float _lastClickTime = -1f;

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                if (BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase))
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} VR では H の進行の診断ログを出しません（通常版の HSceneProc だけが対象）。");
                    return;
                }
                MethodInfo update = AccessTools.Method(typeof(HSceneProc), "Update", Type.EmptyTypes);
                _lstProc = AccessTools.Field(typeof(HSceneProc), "lstProc");
                if (update == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} HSceneProc.Update が見つからないため、H の進行の診断ログを出しません。");
                    return;
                }
                harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.Method(typeof(HStallDiag), nameof(UpdatePrefix))));
                PatcherPlugin.Logger.LogInfo($"{Tag} HSceneProc.Update に H の進行の診断ログを適用しました（設定 {(PatcherPlugin.EnableHStallDiagnostics.Value ? "有効" : "無効")}、" +
                                             $"{PatcherPlugin.HStallSeconds.Value}秒とどまったら出す）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        /// <summary>ksPlug の型（あれば）。診断ログに ksPlug の操作を止める条件を出すため。</summary>
        internal static void BindKsPlug(Assembly asm, string root)
        {
            try
            {
                Type core = asm.GetType(root + ".Core");
                Type dealer = asm.GetType(root + ".Dealer");
                _hCtrlType = asm.GetType(root + ".HSession.HCtrl");
                Type aiMain = asm.GetType(root + ".Character.AIMain");
                Type aiGuest = asm.GetType(root + ".Character.AIGuest");
                _coreDealer = core != null ? AccessTools.Field(core, "dealer") : null;
                _dealerCtx = dealer != null ? AccessTools.Property(dealer, "Ctx") : null;
                _dealerHCtrl = dealer != null ? AccessTools.Property(dealer, "HCtrl") : null;
                _dealerInProcess = dealer != null ? AccessTools.Property(dealer, "InProcess") : null;
                _hCtrlCursorOverUIS = _hCtrlType != null ? AccessTools.Property(_hCtrlType, "CursorOverUIS") : null;
                _mainAiInDecision = aiMain != null ? AccessTools.Method(aiMain, "MainAiInDecision", Type.EmptyTypes) : null;
                _areGuestInKiss = aiGuest != null ? AccessTools.Method(aiGuest, "AreGuestInKiss", Type.EmptyTypes) : null;
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} ksPlug の型の取得に失敗しました（ksPlug の項目は出しません）: {ex.Message}");
            }
        }

        private static void UpdatePrefix(HSceneProc __instance)
        {
            if (!PatcherPlugin.EnableHStallDiagnostics.Value || PatcherPlugin.IsQuitting) return;
            try
            {
                HFlag flags = __instance.flags;
                if (flags == null) return;
                float now = Time.realtimeSinceStartup;
                if (!ReferenceEquals(__instance, _proc))
                {
                    _proc = __instance;
                    _state = null;
                    _clickLogs = 0;
                    _voiceWaitLogs = 0;
                    _lastVoiceWait = flags.voiceWait;
                    _lastClick = HFlag.ClickKind.none;
                    _lastClickText = "なし";
                    _lastClickTime = -1f;
                }
                string state = flags.nowAnimStateName ?? "";
                if (state != _state)
                {
                    _state = state;
                    _stateSince = now;
                    _dumped = false;
                    _stallClickLogs = 0;
                }

                // H の操作（ゲームのボタン・ksPlug の操作のどちらでも flags.click に入る。HSceneProc.Update の最後で none に戻る）
                if (flags.click != _lastClick)
                {
                    _lastClick = flags.click;
                    if (flags.click != HFlag.ClickKind.none)
                    {
                        _lastClickText = $"{flags.click}（状態 {state}）";
                        _lastClickTime = now;
                        if (_clickLogs < MaxClickLogsPerH)
                        {
                            _clickLogs++;
                            PatcherPlugin.Logger.LogInfo($"{Tag} H の操作 {flags.click}（状態 {state}、mode {flags.mode}、frame {Time.frameCount}）" +
                                                         (_clickLogs == MaxClickLogsPerH ? "。この H では以降は出しません" : ""));
                        }
                    }
                }

                // voiceWait の変化（前の HSceneProc.Update からこのフレームまでの間に、ゲーム・ksPlug・他の MOD のどれかが変えた）
                if (flags.voiceWait != _lastVoiceWait)
                {
                    _lastVoiceWait = flags.voiceWait;
                    if (_voiceWaitLogs < MaxVoiceWaitLogsPerH)
                    {
                        _voiceWaitLogs++;
                        PatcherPlugin.Logger.LogInfo($"{Tag} voiceWait {(flags.voiceWait ? "false → true" : "true → false")}（状態 {state}、mode {flags.mode}、rePlay {Safe(() => GetRePlay(__instance, flags))}、" +
                                                     $"click {flags.click}、体位の選択待ち {flags.selectAnimationListInfo != null}、frame {Time.frameCount}）" +
                                                     (_voiceWaitLogs == MaxVoiceWaitLogsPerH ? "。この H では以降は出しません" : ""));
                    }
                }

                if (!IsWatched(state, flags)) return;
                float stay = now - _stateSince;
                if (!_dumped && stay >= PatcherPlugin.HStallSeconds.Value)
                {
                    _dumped = true;
                    PatcherPlugin.Logger.LogInfo(BuildDump(__instance, flags, state, stay));
                }
                if (_dumped && Input.GetMouseButtonUp(0) && _stallClickLogs < MaxStallClickLogs)
                {
                    _stallClickLogs++;
                    PatcherPlugin.Logger.LogInfo($"{Tag} とどまっている間の左クリック（{_stallClickLogs}回目、状態 {state}、{stay:F0}秒）: マウスの下の UI {DescribeUiUnderMouse()} / " +
                                                 $"{DescribeScene(__instance)} / flags.click {flags.click}");
                }
            }
            catch (Exception ex)
            {
                LogExceptionOnce(ex);
            }
        }

        /// <summary>止まっていると気づきたい状態: 射精の前のループ、射精の後のループ（声の終わりを待つ）、声の待ち。</summary>
        private static bool IsWatched(string state, HFlag flags)
        {
            return state.Contains("OLoop") || state.EndsWith("IN_Loop") || state.EndsWith("OUT_Loop") || flags.voiceWait;
        }

        private static string BuildDump(HSceneProc proc, HFlag flags, string state, float stay)
        {
            var sb = new StringBuilder();
            sb.Append($"{Tag} 状態 {state} に {stay:F0}秒とどまっています（frame {Time.frameCount}）。");
            sb.Append($"\n  H: mode {flags.mode}、rePlay {Safe(() => GetRePlay(proc, flags))}、click {flags.click}、最後の操作 {_lastClickText}");
            if (_lastClickTime >= 0f) sb.Append($"（{Time.realtimeSinceStartup - _lastClickTime:F0}秒前）");
            sb.Append($"、voiceWait {flags.voiceWait}、finish {flags.finish}、ゲージ 男 {flags.gaugeMale:F0}・女 {flags.gaugeFemale:F0}、isHSceneEnd {flags.isHSceneEnd}、timeScale {Time.timeScale}");
            sb.Append($"\n  声: {Safe(() => DescribeVoices(proc, flags))}");
            sb.Append($"\n  シーン: {DescribeScene(proc)}");
            sb.Append($"\n  ボタン: {Safe(() => DescribeButtons(proc.sprite))}");
            sb.Append($"\n  ksPlug: {Safe(DescribeKsPlug)}");
            sb.Append($"\n  UI: 選択中 {Safe(() => EventSystem.current != null ? PathOf(EventSystem.current.currentSelectedGameObject) : "EventSystem なし")}、マウスの下 {DescribeUiUnderMouse()}");
            return sb.ToString();
        }

        private static string GetRePlay(HSceneProc proc, HFlag flags)
        {
            var list = _lstProc != null ? _lstProc.GetValue(proc) as IList : null;
            int mode = (int)flags.mode;
            if (list == null || mode < 0 || mode >= list.Count || list[mode] == null) return "?";
            object action = list[mode];
            FieldInfo f = AccessTools.Field(action.GetType(), "rePlay");
            return $"{(f != null ? Convert.ToString(f.GetValue(action)) : "なし")}（{action.GetType().Name}）";
        }

        private static string DescribeVoices(HSceneProc proc, HFlag flags)
        {
            var parts = new List<string>();
            HVoiceCtrl voice = proc.voice;
            for (int i = 0; i < 2; i++)
            {
                string state = voice != null && voice.nowVoices != null && i < voice.nowVoices.Length && voice.nowVoices[i] != null ? voice.nowVoices[i].state.ToString() : "?";
                Transform t = flags.transVoiceMouth != null && i < flags.transVoiceMouth.Length ? flags.transVoiceMouth[i] : null;
                string playing = t != null ? (Manager.Voice.IsPlay(t) ? "再生中" : "なし") : "口なし";
                parts.Add($"枠{i} {state}・口の声 {playing}");
            }
            parts.Add($"どこかで再生中 {Manager.Voice.IsPlay()}");
            return string.Join(" / ", parts.ToArray());
        }

        private static string DescribeScene(HSceneProc proc)
        {
            return Safe(() =>
            {
                string names = Manager.Scene.NowSceneNames != null ? string.Join(",", Manager.Scene.NowSceneNames.ToArray()) : "?";
                string sprite = proc.sprite != null ? $"、HSprite の操作 {proc.sprite.IsSpriteAciotn()}・フェード {proc.sprite.GetFadeKindProc()}" : "、HSprite なし";
                return $"シーン [{names}]、AddSceneName '{Manager.Scene.AddSceneName}'、読み込み中 {Manager.Scene.IsNowLoading}・フェード {Manager.Scene.IsNowLoadingFade}・重なり {Manager.Scene.IsOverlap}{sprite}";
            });
        }

        /// <summary>終了ボタンと、表示中の各体位の吹き出しボタン（categoryActionButton）の表示・押せるか。</summary>
        private static string DescribeButtons(HSprite sprite)
        {
            if (sprite == null) return "HSprite なし";
            var parts = new List<string>();
            parts.Add($"終了 {DescribeButton(sprite.btnEnd)}");
            foreach (FieldInfo f in typeof(HSprite).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object group = f.GetValue(sprite);
                if (group == null) continue;
                FieldInfo catField = group.GetType().GetField("categoryActionButton", BindingFlags.Public | BindingFlags.Instance);
                var cat = catField != null ? catField.GetValue(group) as HSceneSpriteCategory : null;
                if (cat == null || !cat.gameObject.activeInHierarchy || cat.lstButton == null) continue;
                var buttons = new List<string>();
                for (int i = 0; i < cat.lstButton.Count; i++) buttons.Add($"{i}:{DescribeButton(cat.lstButton[i])}");
                parts.Add($"{f.Name} [{string.Join(" ", buttons.ToArray())}]");
            }
            return string.Join(" / ", parts.ToArray());
        }

        private static string DescribeButton(Button b)
        {
            if (b == null) return "なし";
            if (!b.gameObject.activeInHierarchy) return "非表示";
            return b.interactable ? "押せる" : "押せない";
        }

        private static string DescribeKsPlug()
        {
            if (_coreDealer == null) return "なし";
            var dealer = _coreDealer.GetValue(null) as Component;
            if (dealer == null) return "Dealer なし";
            var sb = new StringBuilder();
            sb.Append($"Ctx {(_dealerCtx != null ? Convert.ToString(_dealerCtx.GetValue(dealer, null)) : "?")}");
            if (_dealerInProcess != null) sb.Append($"、交代中など {_dealerInProcess.GetValue(dealer, null)}");
            if (_hCtrlType != null) sb.Append($"、H の制御 {dealer.gameObject.GetComponents(_hCtrlType).Length}個");
            object hCtrl = _dealerHCtrl != null ? _dealerHCtrl.GetValue(dealer, null) : null;
            if (hCtrl as UnityEngine.Object != null && _hCtrlCursorOverUIS != null) sb.Append($"、UI の上 {Safe(() => Convert.ToString(_hCtrlCursorOverUIS.GetValue(hCtrl, null)))}");
            if (_mainAiInDecision != null) sb.Append($"、メインの AI が判断中 {Safe(() => Convert.ToString(_mainAiInDecision.Invoke(null, null)))}");
            if (_areGuestInKiss != null) sb.Append($"、招待キャラとキス中 {Safe(() => Convert.ToString(_areGuestInKiss.Invoke(null, null)))}");
            return sb.ToString();
        }

        private static string DescribeUiUnderMouse()
        {
            return Safe(() =>
            {
                EventSystem es = EventSystem.current;
                if (es == null) return "（EventSystem なし）";
                var data = new PointerEventData(es) { position = Input.mousePosition };
                var hits = new List<RaycastResult>();
                es.RaycastAll(data, hits);
                if (hits.Count == 0) return "（なし）";
                var parts = new List<string>();
                for (int i = 0; i < hits.Count && i < 3; i++) parts.Add(PathOf(hits[i].gameObject));
                return $"{hits.Count}件: {string.Join(" > ", parts.ToArray())}";
            });
        }

        private static string PathOf(GameObject go)
        {
            if (go == null) return "なし";
            var names = new List<string>();
            for (Transform t = go.transform; t != null && names.Count < 5; t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names.ToArray());
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); }
            catch (Exception ex) { return $"（取得できず: {ex.GetType().Name}）"; }
        }

        private static bool _exceptionLogged;

        private static void LogExceptionOnce(Exception ex)
        {
            if (_exceptionLogged) return;
            _exceptionLogged = true;
            PatcherPlugin.Logger.LogWarning($"{Tag} 診断ログの処理中に例外が起きました（以降は出しません。ゲームの処理には影響しません）: {ex}");
        }
    }
}
