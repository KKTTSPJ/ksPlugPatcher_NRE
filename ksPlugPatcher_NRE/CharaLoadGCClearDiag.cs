using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// H 中の着替えで、使っていない画像の掃除（Resources.UnloadUnusedAssets）が動かない原因を調べる診断ログ（0.9.0。回避策ではない）。
    /// 背景: docs/KSPLUG_ISSUES.md §13
    ///
    /// 【問題】
    ///   KK では H の着替えのたびに UnloadUnusedAssets が動くが、KKS の H 中は一度も動かず、古い服の RenderTexture などが H を出るまで残る
    ///   （女6人で着替えを続けて VRAM +5.6GB。VR でも同じ）。
    ///   ゲーム本体の着替え（ChangeCoordinateTypeAndReload → Reload → ReloadNoAsync）は、<c>Manager.Character.enableCharaLoadGCClear</c> が true のときだけ掃除する。
    ///   ゲーム本体で true に戻すのは ActionScene・NPC・Cycle・CustomScene・Studio で、H には無い。原因は、KKS の Character.BeginLoadAssetBundle(_resources: true) が false にして戻さないこと（この診断ログで分かった。対策は HOutfitCleanup）。
    ///
    /// 【ログ】
    ///   - 値が変わったフレームで、前後の値・シーン・時刻（毎フレーム値を読んで比べる）。起動後の最初の値も出す。
    ///   - ChangeCoordinateTypeAndReload の後に、そのときの値と戻り値（着替えたか）・キャラ名（シーンごとに上限まで）。
    ///   掃除が実際に動いたかは、IllusionFixes の「Starting unused asset cleanup / Skipping cleanup」と Unity の「Unloading N unused Assets」の行で見る。
    /// </summary>
    internal static class CharaLoadGCClearDiag
    {
        private const string Tag = "[CharaLoadGCClear]";
        private const int MaxReloadLogsPerScene = 30;

        private static Func<bool> _get;
        private static bool _known, _last;
        private static string _logScene;
        private static int _logCount;

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                Type character = AccessTools.TypeByName("Manager.Character");
                PropertyInfo prop = character != null ? AccessTools.Property(character, "enableCharaLoadGCClear") : null;
                MethodInfo getter = prop != null ? prop.GetGetMethod(true) : null;
                if (getter == null || !getter.IsStatic || getter.ReturnType != typeof(bool))
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} Manager.Character.enableCharaLoadGCClear が見つからないため、診断ログは出しません。");
                    return;
                }
                _get = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);

                int patched = 0;
                foreach (MethodInfo m in typeof(ChaControl).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "ChangeCoordinateTypeAndReload" || m.ReturnType != typeof(bool)) continue;
                    harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(CharaLoadGCClearDiag), nameof(ReloadPostfix))));
                    patched++;
                }
                PatcherPlugin.Logger.LogInfo($"{Tag} 着替えの後の掃除の設定値（Manager.Character.enableCharaLoadGCClear）の診断ログを用意しました（今の値={_get()}、ChangeCoordinateTypeAndReload {patched}個）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
                _get = null;
            }
        }

        /// <summary>毎フレーム（PatcherPlugin.Update）。値が変わったら出す。</summary>
        internal static void OnUpdate()
        {
            if (_get == null || !PatcherPlugin.EnableCharaLoadGCClearDiagnostics.Value) return;
            try
            {
                bool v = _get();
                if (_known && v == _last) return;
                PatcherPlugin.Logger.LogInfo(_known
                    ? $"{Tag} 値が {_last} → {v} に変わりました（シーン={SceneManager.GetActiveScene().name}、frame {Time.frameCount}、{DateTime.Now:HH:mm:ss}）。"
                    : $"{Tag} 今の値は {v} です（シーン={SceneManager.GetActiveScene().name}、frame {Time.frameCount}、{DateTime.Now:HH:mm:ss}）。");
                _known = true;
                _last = v;
            }
            catch { }
        }

        private static void ReloadPostfix(ChaControl __instance, bool __result)
        {
            if (_get == null || !PatcherPlugin.EnableCharaLoadGCClearDiagnostics.Value) return;
            try
            {
                string scene = SceneManager.GetActiveScene().name;
                if (scene != _logScene) { _logScene = scene; _logCount = 0; }
                if (_logCount >= MaxReloadLogsPerScene) return;
                _logCount++;
                string name = __instance != null && __instance.chaFile != null ? __instance.chaFile.parameter.fullname : "?";
                PatcherPlugin.Logger.LogInfo($"{Tag} 着替え（ChangeCoordinateTypeAndReload）: {name} / 着替えた={__result} / 掃除の設定値={_get()}" +
                    $"（シーン={scene}、{_logCount}/{MaxReloadLogsPerScene}、{DateTime.Now:HH:mm:ss}）");
            }
            catch { }
        }
    }
}
