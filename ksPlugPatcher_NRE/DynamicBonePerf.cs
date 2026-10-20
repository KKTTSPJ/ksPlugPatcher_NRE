using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// ksPlug で多人数を招待したときの揺れもの（ゲーム本体の DynamicBone）の負荷の計測と、その負荷を下げる回避策（0.3.0）。
    /// KK の kPlugPatcher_InviteChrID の KPlugPerfDiagnostics（R3）と DynamicBoneThrottle（D1）の KKS 版。
    ///
    /// 【背景（KK の kPlug での計測）】
    ///   招待キャラ1人ごとに DynamicBone が約60〜70増え、5人のときフレーム時間の約6割が DynamicBone.LateUpdate（75〜94ms/f）だった。
    ///   DynamicBone.UpdateDynamicBones は m_UpdateRate（既定60回/秒）に合わせ、フレームが遅いと1フレームで最大3回計算するため、
    ///   フレームレートが下がるほど計算が増える悪循環になる。招待キャラの揺れものを止め、計算回数を1回に抑えると 4〜5倍速くなった。
    ///
    /// 【KKS】
    ///   KKS の DynamicBone / DynamicBone_Ver01 の UpdateDynamicBones は KK と同じ（最大3回）。ksPlug は胸（DynamicBone_Ver02）を少し動かすだけで、
    ///   髪・服などの揺れものを減らす処理は無い。同じ問題が起きる条件はそろっているが、実測はまだ。
    ///
    /// 【計測（設定 EnablePerfDiagnostics、既定 false）】
    ///   H の間（HSceneProc がある間）、30秒ごとに、平均フレーム時間・キャラの数（女性・男性、ksPlug の招待キャラ AIGuest）・
    ///   揺れものの数・DynamicBone / Ver01 / Ver02 の LateUpdate の合計時間と呼び出し回数・物理（FixedUpdate）の回数を出す。
    ///
    /// 【回避策（既定では 1 と 2 が有効、胸は無効。F1 の設定画面でゲーム中に切り替えられ、次のフレームから効く）】
    ///   1. DisableGuestDynamicBones: ksPlug の招待キャラ（AIGuest が付いた ChaControl）の DynamicBone / DynamicBone_Ver01 の LateUpdate（揺れの計算と反映）を行わない。
    ///      Update（InitTransforms：ボーンを初期の姿勢に戻す）は動くので、揺れものは初期の姿勢で静止する。メインのヒロイン（AIMain）と男性は対象外。
    ///      DisableGuestBreastDynamicBones で胸（DynamicBone_Ver02）も止める。
    ///   2. LimitDynamicBoneIterations: すべてのキャラの DynamicBone / DynamicBone_Ver01 の1フレームの計算回数を MaxIterationsPerFrame までに抑える。
    ///      フレームレートが m_UpdateRate（60）以上なら元々1回以下なので、見た目は変わらない。
    /// </summary>
    internal static class DynamicBonePerf
    {
        private const string Tag = "[DynamicBonePerf]";
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private const float SummaryIntervalSeconds = 30f;

        private static Type _aiGuestType;

        // 計測
        private static long _ticksDb, _ticksDb1, _ticksDb2;
        private static int _callsDb, _callsDb1, _callsDb2;
        private static int _skipped;
        private static int _frames, _fixedSteps;
        private static float _windowStart = -1f;

        // 招待キャラの判定のキャッシュ（DynamicBone の InstanceID → 招待キャラのものか）
        private static readonly Dictionary<int, bool> _guestCache = new Dictionary<int, bool>();
        private static float _cacheClearedAt;
        private const float CacheLifetimeSeconds = 5f;

        private static AccessTools.FieldRef<DynamicBone, float> _dbTime, _dbRate;
        private static AccessTools.FieldRef<DynamicBone_Ver01, float> _db1Time, _db1Rate;

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                Assembly asm = PatcherPlugin.FindKsPlugAssembly(out string root);
                _aiGuestType = asm != null ? asm.GetType(root + ".Character.AIGuest") : null;
                if (_aiGuestType == null)
                    PatcherPlugin.Logger.LogInfo($"{Tag} {root}.Character.AIGuest が見つからないため、招待キャラの判定はできません（招待キャラの揺れものの停止は効きません）。");

                var timeStart = new HarmonyMethod(AccessTools.Method(typeof(DynamicBonePerf), nameof(TimeStartPrefix))) { priority = Priority.First };
                var guestSkip = new HarmonyMethod(AccessTools.Method(typeof(DynamicBonePerf), nameof(GuestSkipPrefix)));
                var guestSkipBreast = new HarmonyMethod(AccessTools.Method(typeof(DynamicBonePerf), nameof(GuestSkipBreastPrefix)));
                harmony.Patch(AccessTools.Method(typeof(DynamicBone), "LateUpdate"), prefix: timeStart,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(DynamicBonePerf), nameof(TimeEndDb))));
                harmony.Patch(AccessTools.Method(typeof(DynamicBone), "LateUpdate"), prefix: guestSkip);
                harmony.Patch(AccessTools.Method(typeof(DynamicBone_Ver01), "LateUpdate"), prefix: timeStart,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(DynamicBonePerf), nameof(TimeEndDb1))));
                harmony.Patch(AccessTools.Method(typeof(DynamicBone_Ver01), "LateUpdate"), prefix: guestSkip);
                harmony.Patch(AccessTools.Method(typeof(DynamicBone_Ver02), "LateUpdate"), prefix: timeStart,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(DynamicBonePerf), nameof(TimeEndDb2))));
                harmony.Patch(AccessTools.Method(typeof(DynamicBone_Ver02), "LateUpdate"), prefix: guestSkipBreast);

                _dbTime = AccessTools.FieldRefAccess<DynamicBone, float>("m_Time");
                _dbRate = AccessTools.FieldRefAccess<DynamicBone, float>("m_UpdateRate");
                _db1Time = AccessTools.FieldRefAccess<DynamicBone_Ver01, float>("m_Time");
                _db1Rate = AccessTools.FieldRefAccess<DynamicBone_Ver01, float>("m_UpdateRate");
                harmony.Patch(AccessTools.Method(typeof(DynamicBone), "UpdateDynamicBones", new[] { typeof(float) }),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(DynamicBonePerf), nameof(LimitPrefixDb))));
                harmony.Patch(AccessTools.Method(typeof(DynamicBone_Ver01), "UpdateDynamicBones", new[] { typeof(float) }),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(DynamicBonePerf), nameof(LimitPrefixDb1))));

                PatcherPlugin.Logger.LogInfo($"{Tag} 揺れもの（DynamicBone / Ver01 / Ver02）に計測と回避策を適用しました（{State()}）。設定は F1 の画面でゲーム中に切り替えられます。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        private static string State() =>
            $"計測={(PatcherPlugin.EnablePerfDiagnostics.Value ? "有効" : "無効")}" +
            $" / 招待キャラの揺れものの停止={(PatcherPlugin.DisableGuestDynamicBones.Value ? "有効" : "無効")}（胸も停止={PatcherPlugin.DisableGuestBreastDynamicBones.Value}）" +
            $" / 1フレームの計算回数の上限={(PatcherPlugin.LimitDynamicBoneIterations.Value ? Math.Max(1, PatcherPlugin.MaxDynamicBoneIterationsPerFrame.Value) + "回" : "無効")}";

        // ===================== 計測 =====================

        private static void TimeStartPrefix(out long __state)
        {
            __state = PatcherPlugin.EnablePerfDiagnostics.Value ? Stopwatch.GetTimestamp() : 0;
        }

        private static void TimeEndDb(long __state) { if (__state != 0) { _ticksDb += Stopwatch.GetTimestamp() - __state; _callsDb++; } }
        private static void TimeEndDb1(long __state) { if (__state != 0) { _ticksDb1 += Stopwatch.GetTimestamp() - __state; _callsDb1++; } }
        private static void TimeEndDb2(long __state) { if (__state != 0) { _ticksDb2 += Stopwatch.GetTimestamp() - __state; _callsDb2++; } }

        /// <summary>PatcherPlugin.Update から毎フレーム呼ぶ。</summary>
        internal static void OnUpdate()
        {
            if (!PatcherPlugin.EnablePerfDiagnostics.Value) { _windowStart = -1f; return; }
            float now = Time.realtimeSinceStartup;
            if (_windowStart < 0f) { ResetWindow(now); return; }
            _frames++;
            if (now - _windowStart < SummaryIntervalSeconds) return;
            try
            {
                if (UnityEngine.Object.FindObjectOfType<HSceneProc>() != null) Report(now - _windowStart);
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 計測のまとめに失敗しました: {ex.Message}");
            }
            ResetWindow(now);
        }

        /// <summary>
        /// 揺れものの設定が変わったとき（PatcherPlugin の設定の監視から呼ぶ）。そこまでの分を「設定の変更まで」のまとめとして出し、計測の区切りを新しく始める。
        /// 30秒の途中で切り替えても、1行に切り替え前後の値が混ざらないようにするため。
        /// </summary>
        internal static void OnSettingChanged(string key)
        {
            try
            {
                if (_windowStart < 0f) return;
                float now = Time.realtimeSinceStartup;
                if (_frames > 0 && UnityEngine.Object.FindObjectOfType<HSceneProc>() != null)
                    Report(now - _windowStart, $"設定（{key}）の変更まで");
                ResetWindow(now);
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 設定の変更での計測の区切りに失敗しました: {ex.Message}");
            }
        }

        /// <summary>PatcherPlugin.FixedUpdate から呼ぶ。</summary>
        internal static void OnFixedUpdate()
        {
            if (_windowStart >= 0f) _fixedSteps++;
        }

        private static void ResetWindow(float now)
        {
            _windowStart = now;
            _frames = 0;
            _fixedSteps = 0;
            _ticksDb = _ticksDb1 = _ticksDb2 = 0;
            _callsDb = _callsDb1 = _callsDb2 = 0;
            _skipped = 0;
        }

        private static void Report(float seconds, string label = null)
        {
            int frames = Math.Max(1, _frames);
            int female = 0, male = 0, guests = 0;
            foreach (ChaControl cha in UnityEngine.Object.FindObjectsOfType<ChaControl>())
            {
                if (cha == null || !cha.visibleAll) continue;
                if (cha.sex == 1) female++; else male++;
                if (_aiGuestType != null && cha.GetComponent(_aiGuestType) != null) guests++;
            }
            int db = 0, db1 = 0, db2 = 0;
            foreach (DynamicBone d in UnityEngine.Object.FindObjectsOfType<DynamicBone>()) if (d.enabled) db++;
            foreach (DynamicBone_Ver01 d in UnityEngine.Object.FindObjectsOfType<DynamicBone_Ver01>()) if (d.enabled) db1++;
            foreach (DynamicBone_Ver02 d in UnityEngine.Object.FindObjectsOfType<DynamicBone_Ver02>()) if (d.enabled) db2++;

            double frameMs = seconds * 1000.0 / frames;
            PatcherPlugin.Logger.LogInfo(
                $"{Tag}[計測] {(label != null ? label + "の" : "直近 ")}{seconds:F0}秒: 平均フレーム {frameMs:F1}ms（{1000.0 / frameMs:F0}fps） / " +
                $"表示中のキャラ 女{female}・男{male}（うち ksPlug の招待キャラ {guests}） / " +
                $"揺れもの DynamicBone {db}・Ver01 {db1}・Ver02(胸など) {db2} / " +
                $"LateUpdate DynamicBone {_ticksDb * TicksToMs / frames:F1}ms/f（{(double)_callsDb / frames:F0}回/f）・" +
                $"Ver01 {_ticksDb1 * TicksToMs / frames:F1}ms/f・Ver02 {_ticksDb2 * TicksToMs / frames:F1}ms/f / " +
                $"招待キャラの分を止めた {(double)_skipped / frames:F0}回/f / 物理 FixedUpdate {(double)_fixedSteps / frames:F2}回/f / {State()}");
        }

        // ===================== 1. 招待キャラの揺れものを止める =====================

        private static bool GuestSkipPrefix(Component __instance)
        {
            if (!PatcherPlugin.DisableGuestDynamicBones.Value || PatcherPlugin.IsQuitting) return true;
            return !SkipAsGuest(__instance);
        }

        private static bool GuestSkipBreastPrefix(Component __instance)
        {
            if (!PatcherPlugin.DisableGuestDynamicBones.Value || !PatcherPlugin.DisableGuestBreastDynamicBones.Value || PatcherPlugin.IsQuitting) return true;
            return !SkipAsGuest(__instance);
        }

        private static bool SkipAsGuest(Component db)
        {
            try
            {
                if (_aiGuestType == null || db == null) return false;
                float now = Time.realtimeSinceStartup;
                if (now - _cacheClearedAt > CacheLifetimeSeconds)
                {
                    // 招待・退場・交代で持ち主が変わりうるので、定期的に判定し直す
                    _guestCache.Clear();
                    _cacheClearedAt = now;
                }
                int id = db.GetInstanceID();
                if (!_guestCache.TryGetValue(id, out bool skip))
                {
                    ChaControl cha = db.GetComponentInParent<ChaControl>();
                    skip = cha != null && cha.GetComponent(_aiGuestType) != null;
                    _guestCache[id] = skip;
                }
                if (skip) _skipped++;
                return skip;
            }
            catch
            {
                return false;
            }
        }

        // ===================== 2. 1フレームの計算回数を抑える =====================

        private static void LimitPrefixDb(DynamicBone __instance, float t)
        {
            if (!PatcherPlugin.LimitDynamicBoneIterations.Value) return;
            try { Limit(ref _dbTime(__instance), _dbRate(__instance), t); } catch { }
        }

        private static void LimitPrefixDb1(DynamicBone_Ver01 __instance, float t)
        {
            if (!PatcherPlugin.LimitDynamicBoneIterations.Value) return;
            try { Limit(ref _db1Time(__instance), _db1Rate(__instance), t); } catch { }
        }

        /// <summary>
        /// 元の処理は「m_Time += t; while (m_Time >= 1/rate) { m_Time -= 1/rate; 回数++; 3回で打ち切り }」。
        /// 呼ぶ前に m_Time を「m_Time + t &lt; (上限+1)/rate」になるよう切り詰めておけば、計算は上限の回数以下になる（KK と同じ）。
        /// </summary>
        private static void Limit(ref float time, float rate, float t)
        {
            if (rate <= 0f) return;
            int max = Math.Max(1, PatcherPlugin.MaxDynamicBoneIterationsPerFrame.Value);
            if (max >= 3) return;
            float dt = 1f / rate;
            float cap = dt * (max + 1) - 1e-5f - t;
            if (time > cap) time = cap;
        }
    }
}
