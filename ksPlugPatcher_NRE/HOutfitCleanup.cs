using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// H 中の着替えで出た古い服の画像（RenderTexture・マテリアルなど）が、H を出るまで解放されない問題の回避策（0.10.0）。
    /// 背景: docs/KSPLUG_ISSUES.md §13
    ///
    /// 【原因】
    ///   ゲーム本体の着替え（ChangeCoordinateTypeAndReload → Reload）は、<c>Manager.Character.enableCharaLoadGCClear</c> が true のときだけ
    ///   <c>Resources.UnloadUnusedAssets</c> で使っていない画像を掃除する。ところが KKS の <c>Character.BeginLoadAssetBundle(_resources: true)</c>
    ///   （<c>ChaLoadingTask.Reserve</c> などのキャラの読み込み）がこの値を false にし、H では誰も true に戻さない（KK の BeginLoadAssetBundle は値を変えない）。
    ///   そのため H 中は着替えのたびに古い服の画像が残る（女6人で着替えを続けて VRAM 7.6 → 13.2GB。VR では VRAM が溢れて SteamVR が落ちた）。
    ///   さらに IllusionFixes の ResourceUnloadOptimizations は、RAM の使用率が 75% 未満なら UnloadUnusedAssets を飛ばす（VRAM は見ない）。
    ///
    /// 【処理】
    ///   着替え（ChangeCoordinateTypeAndReload が着替えた）のときに値が false なら（ゲームが自分で掃除しない状態）、最後の着替えから DelaySeconds 後に、
    ///   H の間だけ、まとめて1回 UnloadUnusedAssets を呼ぶ。IllusionFixes が入っていれば、その RAM の判定を通さずに元の関数を呼ぶ（BypassIllusionFixesMemoryCheck）。
    ///   値は変えない（キャラの読み込み中に掃除させないためのゲームの値なので、触らない）。シーンの読み込み中は読み込みが終わるまで待つ。
    ///   掃除の間（0.6〜0.7秒ほど。読み込まれている物の数で決まり、解放する量にはよらない）止まる。KK は着替えのたびに同じだけ止まる。
    ///
    /// 【掃除の時期（0.10.1、設定 Timing）】
    ///   0.10.0 の「最後の着替えの DelaySeconds 後」は、着替えが終わって落ち着いた頃に一瞬止まるので目立った（VR で確認）。
    ///   - WithNextChange（既定）: 前の着替えから DelaySeconds 以上たってからの次の着替えの直後に掃除する（ゲーム本体も本来は着替えの最後に掃除する作り）。
    ///     着替えそのものの停止（VR で 1.7〜6.4秒）に続けて止まるので、一続きの停止に見える。続けて着替える間（DelaySeconds 未満）は掃除しない。
    ///     最後の着替えの分は、次の着替えか H を出るまで残る（服1回分。積み上がらない）。IdleFallbackSeconds を 0 より大きくすると、着替えが無いままその秒数たったときにも掃除する。
    ///   - AfterDelay: 0.10.0 と同じ（最後の着替えの DelaySeconds 後）。
    /// </summary>
    internal static class HOutfitCleanup
    {
        private const string Tag = "[HOutfitCleanup]";

        internal enum CleanupTiming { WithNextChange, AfterDelay }

        private static Func<bool> _gcClear;
        private static Func<bool> _isNowLoading;
        private static Type[] _hSceneTypes;
        private static FieldInfo _illusionFixesOriginal;

        /// <summary>OnUpdate で掃除する時刻（AfterDelay の待ち、または WithNextChange の IdleFallbackSeconds）。-1 なら無し。</summary>
        private static float _dueTime = -1f;
        private static string _dueReason;
        /// <summary>まだ掃除していない着替えの数（シーンが変われば 0 に戻す）。</summary>
        private static int _pendingChanges;
        private static float _lastChangeTime = -1f;
        private static bool _running;
        private static int _runs;

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                Type character = AccessTools.TypeByName("Manager.Character");
                MethodInfo getter = character != null ? AccessTools.Property(character, "enableCharaLoadGCClear")?.GetGetMethod(true) : null;
                if (getter == null || !getter.IsStatic || getter.ReturnType != typeof(bool))
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} Manager.Character.enableCharaLoadGCClear が見つからないため、H 中の着替えの後の掃除は行いません。");
                    return;
                }
                _gcClear = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);

                Type scene = AccessTools.TypeByName("Manager.Scene");
                MethodInfo loading = scene != null ? AccessTools.Property(scene, "IsNowLoading")?.GetGetMethod(true) : null;
                if (loading != null && loading.IsStatic && loading.ReturnType == typeof(bool))
                    _isNowLoading = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), loading);

                // 通常版は HSceneProc、VR 版は VRHScene
                var hTypes = new System.Collections.Generic.List<Type>();
                foreach (string n in new[] { "HSceneProc", "VRHScene" })
                {
                    Type t = AccessTools.TypeByName(n);
                    if (t != null && typeof(UnityEngine.Object).IsAssignableFrom(t)) hTypes.Add(t);
                }
                _hSceneTypes = hTypes.ToArray();

                Type ruo = AccessTools.TypeByName("IllusionFixes.ResourceUnloadOptimizations");
                _illusionFixesOriginal = ruo != null ? AccessTools.Field(ruo, "_originalUnload") : null;

                int patched = 0;
                foreach (MethodInfo m in typeof(ChaControl).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "ChangeCoordinateTypeAndReload" || m.ReturnType != typeof(bool)) continue;
                    harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(HOutfitCleanup), nameof(ReloadPostfix))));
                    patched++;
                }
                // シーンが変われば、前の H の分はシーンの切り替えで解放されるので数え直す
                SceneManager.activeSceneChanged += (from, to) => Reset();
                PatcherPlugin.Logger.LogInfo($"{Tag} ChangeCoordinateTypeAndReload {patched}個に適用しました（{State()} / H の判定 {_hSceneTypes.Length}種 / " +
                    $"IllusionFixes の元の掃除の関数={(_illusionFixesOriginal != null ? "あり" : "なし")}）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
                _gcClear = null;
            }
        }

        private static string State() =>
            $"有効={PatcherPlugin.EnableHOutfitCleanup.Value} / 時期={PatcherPlugin.HOutfitCleanupTiming.Value} / 間隔 {PatcherPlugin.HOutfitCleanupDelaySeconds.Value:F1}秒 / " +
            $"着替えが無いときの保険 {(PatcherPlugin.HOutfitCleanupIdleFallbackSeconds.Value > 0f ? PatcherPlugin.HOutfitCleanupIdleFallbackSeconds.Value.ToString("F0") + "秒" : "無効")} / " +
            $"IllusionFixes の RAM の判定を通さない={PatcherPlugin.HOutfitCleanupBypassIllusionFixes.Value}";

        private static void ReloadPostfix(bool __result)
        {
            try
            {
                if (!__result || _gcClear == null || !PatcherPlugin.EnableHOutfitCleanup.Value) return;
                if (_gcClear()) return; // ゲームが自分で掃除する
                float now = Time.realtimeSinceStartup;
                float delay = PatcherPlugin.HOutfitCleanupDelaySeconds.Value;
                if (PatcherPlugin.HOutfitCleanupTiming.Value == CleanupTiming.AfterDelay)
                {
                    _pendingChanges++;
                    _dueTime = now + delay; // 続けて着替えたら数え直す
                    _dueReason = $"最後の着替えから {delay:F0}秒後に";
                }
                else
                {
                    // 前の着替えから delay 以上たっていて、前の分が残っていれば、この着替えの直後に掃除する（着替えの停止に続けて止まる）
                    if (_pendingChanges > 0 && _lastChangeTime >= 0f && now - _lastChangeTime >= delay && !_running && CanRunNow())
                    {
                        Start(_pendingChanges, $"次の着替えと同時に（前の着替えから {now - _lastChangeTime:F0}秒）");
                    }
                    _pendingChanges++; // この着替えの分（次の着替えか H の終わりまで残る）
                    float fallback = PatcherPlugin.HOutfitCleanupIdleFallbackSeconds.Value;
                    _dueTime = fallback > 0f ? now + fallback : -1f;
                    _dueReason = fallback > 0f ? $"着替えが無いまま {fallback:F0}秒たったので" : null;
                }
                _lastChangeTime = now;
            }
            catch { }
        }

        /// <summary>今すぐ掃除してよいか（シーンの読み込み中でなく、H の中で、ゲームが自分で掃除しない状態）。</summary>
        private static bool CanRunNow()
        {
            if (_isNowLoading != null && _isNowLoading()) return false;
            if (_gcClear != null && _gcClear()) return false;
            return InHScene();
        }

        private static void Start(int changes, string reason)
        {
            PatcherPlugin.Instance.StartCoroutine(Run(changes, reason));
            _pendingChanges = 0;
            _dueTime = -1f;
        }

        /// <summary>毎フレーム（PatcherPlugin.Update）。AfterDelay の待ちと、WithNextChange の IdleFallbackSeconds。</summary>
        internal static void OnUpdate()
        {
            if (_dueTime < 0f || _running || Time.realtimeSinceStartup < _dueTime) return;
            try
            {
                if (!PatcherPlugin.EnableHOutfitCleanup.Value || _pendingChanges == 0) { Reset(); return; }
                if (_isNowLoading != null && _isNowLoading()) { _dueTime = Time.realtimeSinceStartup + 1f; return; } // 読み込みが終わるまで待つ
                if (!InHScene())
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} H の外なので、着替え {_pendingChanges}回の後の掃除は行いません（シーンの切り替えで解放されます）。");
                    Reset();
                    return;
                }
                if (_gcClear != null && _gcClear()) { Reset(); return; } // その間にゲームの値が戻った（ゲームが自分で掃除する）
                Start(_pendingChanges, _dueReason ?? "");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 掃除の準備で例外が起きました（今回は行いません）: {ex.Message}");
                Reset();
            }
        }

        private static void Reset()
        {
            _dueTime = -1f;
            _pendingChanges = 0;
            _lastChangeTime = -1f;
        }

        private static bool InHScene()
        {
            foreach (Type t in _hSceneTypes)
            {
                if (UnityEngine.Object.FindObjectOfType(t) != null) return true;
            }
            return false;
        }

        private static IEnumerator Run(int changes, string reason)
        {
            _running = true;
            var sw = Stopwatch.StartNew();
            AsyncOperation op = null;
            string via = "";
            bool failed = false;
            try
            {
                var original = PatcherPlugin.HOutfitCleanupBypassIllusionFixes.Value && _illusionFixesOriginal != null
                    ? _illusionFixesOriginal.GetValue(null) as Func<AsyncOperation>
                    : null;
                if (original != null)
                {
                    op = original();
                    via = "IllusionFixes の RAM の判定を通さずに";
                }
                else
                {
                    op = Resources.UnloadUnusedAssets();
                    via = _illusionFixesOriginal != null ? "IllusionFixes を通して（RAM に余裕があると飛ばされます）" : "";
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 掃除の呼び出しで例外が起きました: {ex.Message}");
                failed = true;
            }
            if (failed)
            {
                _running = false;
                yield break;
            }
            long callMs = sw.ElapsedMilliseconds;
            while (op != null && !op.isDone) yield return null;
            _runs++;
            PatcherPlugin.Logger.LogInfo($"{Tag} H 中の着替え {changes}回の分を、{reason}、使っていない画像の掃除を{via}行いました（呼び出し {callMs}ms・完了まで {sw.ElapsedMilliseconds}ms、累計 {_runs}回）。" +
                "解放した数は直前の Unity の「Unloading N unused Assets」の行。");
            _running = false;
        }
    }
}
