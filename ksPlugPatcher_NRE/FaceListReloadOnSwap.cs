using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// ksPlug のキャラ交代で声のリストだけが読み直され、H の表情のリストが H の開始時の性格のまま残る問題の対策（0.5.0）。
    /// KK の kPlugPatcher_InviteChrID の FaceListReloadOnSwap（v1.8.4）の KKS 版。
    ///
    /// 【原因】H の開始時、ゲーム（KKS の HSceneProc）は表情と声のリストを性格ごとに読み込む。
    /// <code>
    /// yield return face.LoadText(heroine.ChaName);                 // 表情: HFaceList_{cNN}_{kind}_{action}
    /// if (heroine2 != null) yield return face1.LoadText(heroine2.ChaName);
    /// yield return voice.Init(heroine.ChaName, heroine2?.ChaName ?? "");   // 声・吐息・短い吐息
    /// </code>
    /// ksPlug の交代（<c>Character.AIGuest</c>）は <c>StartCoroutine(hCtrl.HProc.voice.Init("c" + 性格, …))</c> だけを呼び、表情のリストを読み直さない
    /// （ksPlug 1.7.0 には表情のリストを読む処理が無い）。そのため交代後のキャラは「新しい性格のセリフ」に「H の開始時の性格の表情番号表」を当てて顔を作る。
    /// 表情番号の意味は性格ごとに違うので、セリフと合わない顔になる。KK_AlternativeVoice の [H読み込み計測] でも、ksPlug の交代の Init では「表情 0回」だった。
    ///
    /// 【対策】KKS の <c>HVoiceCtrl.Init(string, string)</c> はコルーチン（呼んだ時点では何も読まず、MoveNext で読む）。
    /// - Init の Prefix（Priority.First）で、KK_AlternativeVoice がカスタム性格を元の性格に振り替える前の性格名を控え、Postfix で返されたコルーチンに結び付ける。
    /// - そのコルーチンの MoveNext が最後まで進んだら（Finalizer。例外で止まった場合も、声と吐息のリストは読み終わっているので追従させる）、
    ///   その HVoiceCtrl の <c>faceLists[0]</c> / <c>[1]</c> が最後に読んだ性格と比べ、違えば <c>FaceListCtrl.LoadText</c> で読み直す。
    /// - <c>LoadText</c> もコルーチン（中の読み込みは同期、1フレーム待ちが2回）なので、その場で最後まで回す（KK_AlternativeVoice の KKS 版の性格切替と同じ）。
    ///   同期版の <c>LoadTextD</c> は固定キャラの性格の置き換え（fixPersonal）をしないので使わない。
    /// - 表がどの性格かはゲームが覚えていないため、<c>LoadText</c> の Prefix で「最後に読んだ性格名」を記録する。表情を読んだことが無い FaceListCtrl には触らない。
    /// - H の開始時・KK_AlternativeVoice の性格切替は、表情を先に同じ名前で読むので、比較が一致して何もしない（読み込みは増えない）。
    /// - 読み直した結果が空（その性格の表情リストが無い）なら、前の性格で読み直して元に戻す。
    /// </summary>
    internal static class FaceListReloadOnSwap
    {
        private const string Tag = "[FaceListReload]";

        private sealed class Pending
        {
            public HVoiceCtrl Voice;
            public string[] Names;
        }

        /// <summary>FaceListCtrl ごとの「最後に LoadText で読んだ性格名」。キーは Unity のインスタンスID。</summary>
        private static readonly Dictionary<int, string> _loadedNames = new Dictionary<int, string>();
        /// <summary>Init が返したコルーチン → 控えた性格名（コルーチンが要らなくなれば回収される）。</summary>
        private static readonly ConditionalWeakTable<object, Pending> _pending = new ConditionalWeakTable<object, Pending>();
        private static int _reloadCount;
        private static bool _failureLogged;

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                MethodInfo loadText = AccessTools.Method(typeof(FaceListCtrl), "LoadText", new[] { typeof(string) });
                MethodInfo init = AccessTools.Method(typeof(HVoiceCtrl), "Init", new[] { typeof(string), typeof(string) });
                MethodInfo moveNext = init != null && typeof(IEnumerator).IsAssignableFrom(init.ReturnType) ? AccessTools.EnumeratorMoveNext(init) : null;
                if (loadText == null || init == null || moveNext == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} FaceListCtrl.LoadText(string) / HVoiceCtrl.Init(string, string)（コルーチン）が見つからないため、適用しません。");
                    return;
                }
                harmony.Patch(loadText, prefix: new HarmonyMethod(AccessTools.Method(typeof(FaceListReloadOnSwap), nameof(LoadTextPrefix))));
                harmony.Patch(init,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(FaceListReloadOnSwap), nameof(InitPrefix))) { priority = Priority.First },
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(FaceListReloadOnSwap), nameof(InitPostfix))));
                harmony.Patch(moveNext, finalizer: new HarmonyMethod(AccessTools.Method(typeof(FaceListReloadOnSwap), nameof(InitMoveNextFinalizer))));
                PatcherPlugin.Logger.LogInfo($"{Tag} HVoiceCtrl.Init に表情リストの追従を適用しました（ksPlug のキャラ交代で声のリストが読み直されたとき、表情のリストも同じ性格で読み直します）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        private static void LoadTextPrefix(FaceListCtrl __instance, string _chaNo)
        {
            try
            {
                if (__instance != null) _loadedNames[__instance.GetInstanceID()] = _chaNo ?? string.Empty;
            }
            catch { } // 記録できなくても読み込みは止めない（その FaceListCtrl は以後の対象外になるだけ）
        }

        /// <summary>KK_AlternativeVoice が振り替える前の性格名を控える。</summary>
        private static void InitPrefix(string _chaFolder, string _chaFolder1, out string[] __state)
        {
            __state = new[] { _chaFolder, _chaFolder1 };
        }

        private static void InitPostfix(HVoiceCtrl __instance, IEnumerator __result, string[] __state)
        {
            try
            {
                if (__instance == null || __result == null || __state == null || !PatcherPlugin.EnableFaceListReloadOnSwap.Value) return;
                _pending.Remove(__result);
                _pending.Add(__result, new Pending { Voice = __instance, Names = __state });
            }
            catch { }
        }

        /// <summary>Init のコルーチンが最後まで進んだ（または例外で止まった）ら、表情のリストを追従させる。例外はそのまま返す。</summary>
        private static Exception InitMoveNextFinalizer(object __instance, bool __result, Exception __exception)
        {
            if (__exception == null && __result) return null; // まだ途中
            try
            {
                if (__instance == null || !_pending.TryGetValue(__instance, out Pending p)) return __exception;
                _pending.Remove(__instance);
                if (PatcherPlugin.IsQuitting || p.Voice == null || p.Voice.faceLists == null) return __exception;
                for (int i = 0; i < 2 && i < p.Voice.faceLists.Length && i < p.Names.Length; i++)
                    ReloadIfChanged(p.Voice.faceLists[i], i, p.Names[i]);
            }
            catch (Exception ex)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    PatcherPlugin.Logger.LogWarning($"{Tag} 表情リストの読み直しに失敗しました（表情は前の性格のままになります。以降は出しません）: {ex.Message}");
                }
            }
            return __exception;
        }

        private static void ReloadIfChanged(FaceListCtrl face, int slot, string newName)
        {
            if (face == null || string.IsNullOrEmpty(newName)) return;
            if (!_loadedNames.TryGetValue(face.GetInstanceID(), out string oldName)) return; // 表情を読んだことが無い
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;           // H の開始時・同じ性格

            var sw = Stopwatch.StartNew();
            RunNow(face.LoadText(newName));
            int count = CountEntries(face);
            if (count == 0 && !string.IsNullOrEmpty(oldName))
            {
                RunNow(face.LoadText(oldName));
                PatcherPlugin.Logger.LogWarning($"{Tag} 枠{slot}: {newName} の表情リストが見つからないため、{oldName} のまま使います（{CountEntries(face)}件）。");
                return;
            }
            _reloadCount++;
            PatcherPlugin.Logger.LogInfo($"{Tag} 枠{slot}: 表情リストを {oldName} → {newName} に読み直しました（{_reloadCount}回目、{count}件、{sw.ElapsedMilliseconds}ms）。");
        }

        /// <summary>コルーチンをその場で最後まで回す（LoadText は null を返して1フレーム待つだけで、中の読み込みは同期）。入れ子の IEnumerator も回す。</summary>
        private static void RunNow(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            int steps = 0;
            while (stack.Count > 0 && steps++ < 10000)
            {
                IEnumerator current = stack.Peek();
                if (!current.MoveNext()) { stack.Pop(); continue; }
                if (current.Current is IEnumerator nested) stack.Push(nested);
            }
        }

        private static int CountEntries(FaceListCtrl face)
        {
            int count = 0;
            if (face.facelib == null) return 0;
            foreach (var kind in face.facelib)
            {
                if (kind == null) continue;
                foreach (var dic in kind)
                    if (dic != null) count += dic.Count;
            }
            return count;
        }
    }
}
