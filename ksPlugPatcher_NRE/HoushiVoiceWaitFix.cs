using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// ゲーム本体の奉仕（2P・3P）で、<c>flags.voiceWait</c> が true のまま戻らず、H の UI（吹き出しのボタン・終了ボタン・体位のメニュー）が押せなくなる問題の回避策（docs/KSPLUG_ISSUES.md §11）。
    /// ksPlug ではなくゲーム本体（Assembly-CSharp）へのパッチ。招待キャラで遅くなる・長い声を待つ間に連打する、などで起きやすくなる。
    ///
    /// ゲームの作り:
    /// - <c>HSprite.Houshi3PProc</c> などは、voiceWait が true の間、H の UI を押せなくする。
    /// - 速さのパッドの左クリック（<c>HSprite.OnSpeedUpClick</c>）は、奉仕で女性のアニメーターの状態が Idle なら voiceWait を true にする（Idle の処理が声の終わりを待ってから WLoop に進むため）。
    /// - <c>HActionBase.SetPlay</c> は <c>nowAnimStateName</c> をすぐ変えるが、アニメーターの状態は次のアニメーションの更新まで Idle のまま。
    ///   Idle → WLoop に切り替えた同じフレームにパッドが押されると、WLoop に入ったのに voiceWait が true になる。
    /// - WLoop・SLoop・OLoop（射精を選ぶ前）には voiceWait を戻す処理が無いので、そのまま止まる。
    ///
    /// 回避策:
    /// - D. <c>HSprite.OnSpeedUpClick</c> の後で、元の処理が voiceWait を false → true にし、しかも H の処理が既に Idle でなければ、false に戻す。
    /// - E. 保険: 奉仕（houshi・houshi3P）の WLoop・SLoop・OLoop（射精を選ぶ前）で、voiceWait が true、体位の選択の待ちもクリックも無く、
    ///   声の待ちが成り立つ（ゲームの <c>IsCheckVoicePlay</c> と同じ判定）状態が WatchdogSeconds 続いたら、voiceWait を false に戻す。D と別の経路で止まったときのため。
    /// </summary>
    internal static class HoushiVoiceWaitFix
    {
        private const string Tag = "[HoushiVoiceWait]";

        private static FieldInfo _lstProc;     // HSceneProc.lstProc
        private static int _speedUpFixed, _watchdogFixed;

        // E の状態
        private static HSceneProc _proc;
        private static float _stuckSince = -1f;

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                if (BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase))
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} VR では奉仕の voiceWait の回避策を適用しません（通常版の HSprite・HSceneProc だけが対象）。");
                    return;
                }
                MethodInfo speedUp = AccessTools.Method(typeof(HSprite), "OnSpeedUpClick", Type.EmptyTypes);
                MethodInfo update = AccessTools.Method(typeof(HSceneProc), "Update", Type.EmptyTypes);
                _lstProc = AccessTools.Field(typeof(HSceneProc), "lstProc");
                if (speedUp != null)
                {
                    harmony.Patch(speedUp,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(HoushiVoiceWaitFix), nameof(SpeedUpPrefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(HoushiVoiceWaitFix), nameof(SpeedUpPostfix))));
                    PatcherPlugin.Logger.LogInfo($"{Tag} HSprite.OnSpeedUpClick に回避策（D）を適用しました（設定 {(PatcherPlugin.EnableSpeedUpVoiceWaitFix.Value ? "有効" : "無効")}）。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} HSprite.OnSpeedUpClick が見つからないため、回避策（D）を適用しません。");
                }
                if (update != null && _lstProc != null)
                {
                    harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.Method(typeof(HoushiVoiceWaitFix), nameof(UpdatePrefix))));
                    PatcherPlugin.Logger.LogInfo($"{Tag} HSceneProc.Update に voiceWait の保険（E）を適用しました（設定 {(PatcherPlugin.EnableVoiceWaitWatchdog.Value ? "有効" : "無効")}、" +
                                                 $"{PatcherPlugin.VoiceWaitWatchdogSeconds.Value}秒）。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} HSceneProc.Update / lstProc が見つからないため、voiceWait の保険（E）を適用しません。");
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        private static bool IsHoushi(HFlag.EMode mode)
        {
            return mode == HFlag.EMode.houshi || mode == HFlag.EMode.houshi3P || mode == HFlag.EMode.houshi3PMMF;
        }

        // ===================== D =====================

        private static void SpeedUpPrefix(HSprite __instance, out bool __state)
        {
            __state = __instance != null && __instance.flags != null && __instance.flags.voiceWait;
        }

        private static void SpeedUpPostfix(HSprite __instance, bool __state)
        {
            if (!PatcherPlugin.EnableSpeedUpVoiceWaitFix.Value || PatcherPlugin.IsQuitting) return;
            try
            {
                HFlag flags = __instance != null ? __instance.flags : null;
                if (flags == null || __state || !flags.voiceWait || !IsHoushi(flags.mode)) return;
                string state = flags.nowAnimStateName ?? "";
                if (state == "Idle") return; // ゲームの意図どおり（Idle の処理が声の終わりを待つ）
                flags.voiceWait = false;
                _speedUpFixed++;
                if (_speedUpFixed <= 10 || _speedUpFixed % 100 == 0)
                    PatcherPlugin.Logger.LogInfo($"{Tag} 速さのパッドのクリックで voiceWait が立ちましたが、H は既に {state} に進んでいるため戻しました" +
                                                 $"（アニメーターの状態の更新の前のクリック。mode {flags.mode}、frame {Time.frameCount}、{_speedUpFixed}回目）。");
            }
            catch (Exception ex)
            {
                LogExceptionOnce("D", ex);
            }
        }

        // ===================== E =====================

        private static void UpdatePrefix(HSceneProc __instance)
        {
            if (!PatcherPlugin.EnableVoiceWaitWatchdog.Value || PatcherPlugin.IsQuitting) return;
            try
            {
                if (!ReferenceEquals(__instance, _proc))
                {
                    _proc = __instance;
                    _stuckSince = -1f;
                }
                HFlag flags = __instance.flags;
                if (flags == null || !IsStuck(__instance, flags, out string detail))
                {
                    _stuckSince = -1f;
                    return;
                }
                float now = Time.realtimeSinceStartup;
                if (_stuckSince < 0f)
                {
                    _stuckSince = now;
                    return;
                }
                float stuck = now - _stuckSince;
                if (stuck < PatcherPlugin.VoiceWaitWatchdogSeconds.Value) return;
                flags.voiceWait = false;
                _stuckSince = -1f;
                _watchdogFixed++;
                if (_watchdogFixed <= 10 || _watchdogFixed % 100 == 0)
                    PatcherPlugin.Logger.LogInfo($"{Tag} 奉仕の {flags.nowAnimStateName} で voiceWait が {stuck:F1}秒 true のまま（待つ声もクリックも無く、H の UI が押せない状態）だったため、戻しました" +
                                                 $"（mode {flags.mode}、{detail}、frame {Time.frameCount}、{_watchdogFixed}回目）。");
            }
            catch (Exception ex)
            {
                LogExceptionOnce("E", ex);
            }
        }

        /// <summary>
        /// voiceWait を使わない状態（WLoop・SLoop、射精を選ぶ前の OLoop）で voiceWait が true のままか。
        /// 体位の選択の待ち（selectAnimationListInfo。HSceneProc は voiceWait が false になってから体位を変える）とクリックの途中は除く。
        /// </summary>
        private static bool IsStuck(HSceneProc proc, HFlag flags, out string detail)
        {
            detail = "";
            if (!flags.voiceWait || flags.isHSceneEnd) return false;
            if (flags.mode != HFlag.EMode.houshi && flags.mode != HFlag.EMode.houshi3P) return false;
            if (flags.selectAnimationListInfo != null || flags.click != HFlag.ClickKind.none) return false;
            string state = flags.nowAnimStateName ?? "";
            if (state != "WLoop" && state != "SLoop" && state != "OLoop") return false;
            if (state == "OLoop")
            {
                // 射精を選んだ後（rePlay 1・2）は、ゲームが voiceWait を使って声の終わりを待つ
                int rePlay = GetRePlay(proc, flags);
                if (rePlay != 0) return false;
            }
            HVoiceCtrl voice = proc.voice;
            if (voice == null || voice.nowVoices == null) return false;
            int girls = flags.mode == HFlag.EMode.houshi3P ? 2 : 1;
            for (int i = 0; i < girls && i < voice.nowVoices.Length; i++)
            {
                if (voice.nowVoices[i] == null) return false;
                // ゲームの IsCheckVoicePlay と同じ: 吐息なら待たない、セリフなら鳴り終わるまで待つ、短い吐息などは待つ
                HVoiceCtrl.VoiceKind kind = voice.nowVoices[i].state;
                if (kind == HVoiceCtrl.VoiceKind.breath) continue;
                if (kind != HVoiceCtrl.VoiceKind.voice) return false;
                Transform t = flags.transVoiceMouth != null && i < flags.transVoiceMouth.Length ? flags.transVoiceMouth[i] : null;
                if (t == null || Manager.Voice.IsPlay(t)) return false;
            }
            detail = state == "OLoop" ? "射精を選ぶ前" : "ループ中";
            return true;
        }

        /// <summary>体位の処理（HHoushi・H3PHoushi）の rePlay。読めなければ -1（OLoop では戻さない）。</summary>
        private static int GetRePlay(HSceneProc proc, HFlag flags)
        {
            var list = _lstProc.GetValue(proc) as IList;
            int mode = (int)flags.mode;
            if (list == null || mode < 0 || mode >= list.Count || list[mode] == null) return -1;
            FieldInfo f = AccessTools.Field(list[mode].GetType(), "rePlay");
            object v = f != null ? f.GetValue(list[mode]) : null;
            return v is int i ? i : -1;
        }

        private static readonly System.Collections.Generic.HashSet<string> _logged = new System.Collections.Generic.HashSet<string>();

        private static void LogExceptionOnce(string where, Exception ex)
        {
            if (!_logged.Add(where)) return;
            PatcherPlugin.Logger.LogWarning($"{Tag}[{where}] 処理中に例外が起きました（以降は出しません。ゲームの処理には影響しません）: {ex}");
        }
    }
}
