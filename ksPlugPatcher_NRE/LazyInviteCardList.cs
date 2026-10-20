using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// ksPlug の招待メニューが、H に入るたびに全カードを完全な形で読み込んで保持する問題の回避策（0.4.0）。
    /// KK の kPlugPatcher_InviteChrID の LazyInviteCardList（v1.5.0〜v1.7.0）の ksPlug 版。
    ///
    /// 【問題】
    ///   <c>Menu.MenuInvite.Start()</c>（H の開始時・エステ）が、ストーリーの外（フリーH など）では <c>Character.ChaList.GetGirlCardList()</c> /
    ///   <c>GetMaleCardList()</c> で全カードを読む。1枚ごとの <c>ChaList.AddChaFileToList</c> が
    ///   <c>new ChaFileControl().LoadCharaFile(path, sex, false, true)</c> で拡張データまで完全に読み込み、<c>FileInfo.File</c> としてメニューが消えるまで持つ。
    ///   ksPlugVR 1.7.2 は、同じ読み込みを H のキャラ選択（<c>Dealer.SetContext(Context.HSelect)</c>）の <c>MenuInvite.InitFileList()</c> で行う。
    ///   軽量化・読み直しは呼び出し元によらず効く。まとめのログ（0.5.1）は、Start と InitFileList の両方に付ける。
    ///   実機ログ（2回）で、フリーH の1分後に Mono が +7.6GB / +6.9GB
    ///   （332枚・6.9GB、1枚あたり約23MB）。Mono はヒープを OS に返さない。Sideloader の「見つからない MOD」の警告も約1,550件。
    ///
    /// 【回避策】
    ///   1. <c>ChaList.AddChaFileToList</c> を置き換え、一覧には**軽量版**（名前・性格などの parameter と顔画像だけの ChaFileControl）を入れる。
    ///      1枚ずつ軽くするので、読み込み中も完全版は同時に1枚分しか生きない（KK §12.1。まとめて軽くするとピークが下がらない）。
    ///      PartialLoad（既定 true）なら、ファイルから全身画像・顔画像・Parameter ブロックだけを読む（<see cref="TryPartialLoad"/>。ChaFile.LoadFile を通らないので
    ///      拡張データの展開も Sideloader の警告も起きない）。読めないカード（識別子・バージョンが違うなど）は、元どおり完全に読んでから軽量版にする。
    ///      全身画像は顔のテクスチャ（FileInfo.png）を作った後に手放す（ksPlug は一覧のカードの pngData を使わない）。
    ///   2. 招待の唯一の入口 <c>MenuInvite.InviteSomeone(FileInfo, …)</c>（ボタン・ランダム・自動招待とも）の前で、軽量版なら <c>FileInfo.cardPath</c> から
    ///      完全に読み直して <c>FileInfo.File</c> を差し替える。男女とも同じ入口（性別は parameter.sex）。
    ///      読み直した完全版はそのまま一覧に残る（ksPlug は退場時に <c>CleanCreatedFileInfo</c> でキャラの状態をこのカードに書き戻し、次の招待に使う）。
    ///
    /// 【安全策・注意（KK の教訓）】
    ///   - 読み直しの入口（InviteSomeone）にパッチが当たっていなければ軽くしない（軽量版のままキャラが作られると白い既定の姿になる。KK §14・§17.3）。
    ///   - 読み直しは設定を無効にした後も行う（ゲーム中に無効にしても、すでに軽量版になっているカードがあるため）。
    ///   - 軽量版の目印は ConditionalWeakTable で持つ（メニューが消えて軽量版が要らなくなれば、目印ごと回収される）。
    ///   - 軽量版は CopyAll で作らない（KKAPI のフックが拡張データごと複製する。KK §11.4）。
    ///   - ストーリー中（<c>GetGirlSaveList</c>：セーブのヒロインの複製）と衣装メニューは対象外（影響が小さい）。
    /// </summary>
    internal static class LazyInviteCardList
    {
        private const string Tag = "[LazyInviteCards]";
        private const string KksCardMark = "【KoiKatuCharaSun】";

        private static Type _fileInfoType;
        private static FieldInfo _fiFile, _fiPng, _fiFilter, _fiCardPath;
        private static PropertyInfo _charaFileName;
        private static bool _restoreReady;

        /// <summary>軽量版の目印（キーが要らなくなれば値ごと回収される）。</summary>
        private static readonly ConditionalWeakTable<ChaFileControl, object> _light = new ConditionalWeakTable<ChaFileControl, object>();
        private static readonly object Mark = new object();

        // MenuInvite.Start ごとの集計
        private static int _partial, _fullThenLight, _loadFailed, _disabledFull;
        private static long _startTicks;
        private static long _monoBefore;
        private static readonly Dictionary<string, int> _fallbackReasons = new Dictionary<string, int>();
        private static readonly HashSet<string> _loggedOnce = new HashSet<string>();

        internal static void TryApply(Harmony harmony, Assembly asm, string root)
        {
            try
            {
                Type chaList = asm.GetType(root + ".Character.ChaList");
                _fileInfoType = asm.GetType(root + ".Character.ChaList+FileInfo");
                Type menuInvite = asm.GetType(root + ".Menu.MenuInvite");
                MethodInfo add = chaList != null ? AccessTools.Method(chaList, "AddChaFileToList") : null;
                MethodInfo invite = menuInvite != null ? AccessTools.Method(menuInvite, "InviteSomeone") : null;
                MethodInfo start = menuInvite != null ? AccessTools.Method(menuInvite, "Start") : null;
                // ksPlugVR 1.7.2 から: 一覧はキャラ選択（Dealer.SetContext(HSelect)）で InitFileList が読み、Start は読まない
                MethodInfo initFileList = menuInvite != null ? AccessTools.Method(menuInvite, "InitFileList", Type.EmptyTypes) : null;
                if (_fileInfoType != null)
                {
                    _fiFile = AccessTools.Field(_fileInfoType, "File");
                    _fiPng = AccessTools.Field(_fileInfoType, "png");
                    _fiFilter = AccessTools.Field(_fileInfoType, "filter");
                    _fiCardPath = AccessTools.Field(_fileInfoType, "cardPath");
                }
                _charaFileName = AccessTools.Property(typeof(ChaFile), "charaFileName");

                bool addOk = add != null && add.GetParameters().Length == 4 && add.GetParameters()[0].ParameterType == typeof(FolderAssist.FileInfo);
                bool inviteOk = invite != null && invite.GetParameters().Length >= 1 && invite.GetParameters()[0].ParameterType == _fileInfoType;
                if (!addOk || !inviteOk || _fiFile == null || _fiPng == null || _fiFilter == null || _fiCardPath == null || _charaFileName == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {root} の ChaList.AddChaFileToList / FileInfo（File・png・filter・cardPath）/ MenuInvite.InviteSomeone が見つからないため、" +
                        "招待メニューのカードの遅延読み込みは行いません（ksPlug のバージョン違いの可能性）。");
                    return;
                }

                // 先に読み直しの入口を当てる。当たらなければ軽くしない（安全策）
                harmony.Patch(invite, prefix: new HarmonyMethod(AccessTools.Method(typeof(LazyInviteCardList), nameof(InviteSomeonePrefix))));
                _restoreReady = true;
                PatcherPlugin.Logger.LogInfo($"{Tag} {root}.Menu.MenuInvite.InviteSomeone に、招待の前のカードの読み直しを適用しました。");

                harmony.Patch(add, prefix: new HarmonyMethod(AccessTools.Method(typeof(LazyInviteCardList), nameof(AddChaFileToListPrefix))));
                PatcherPlugin.Logger.LogInfo($"{Tag} {root}.Character.ChaList.AddChaFileToList に、一覧のカードの軽量化を適用しました（{State()}）。");

                // まとめのログは、一覧を読む関数ごとに出す
                foreach (MethodInfo m in new[] { start, initFileList })
                {
                    if (m == null) continue;
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(LazyInviteCardList), nameof(StartPrefix))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(LazyInviteCardList), nameof(StartFinalizer))));
                }
                if (initFileList != null)
                    PatcherPlugin.Logger.LogInfo($"{Tag} {root}.Menu.MenuInvite.InitFileList（キャラ選択で一覧を読む版）にも、まとめのログを適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        private static string State() =>
            $"軽量化={(PatcherPlugin.EnableLazyInviteCardList.Value ? "有効" : "無効")} / 部分読み込み={(PatcherPlugin.LazyInvitePartialLoad.Value ? "有効" : "無効")}";

        // ===================== MenuInvite.Start / InitFileList の集計 =====================

        /// <summary>集計する関数の入れ子の深さ（入れ子になっても、外側の1回だけで数え始め・まとめる）。</summary>
        private static int _summaryDepth;

        private static void StartPrefix()
        {
            if (_summaryDepth++ > 0) return;
            _partial = _fullThenLight = _loadFailed = _disabledFull = 0;
            _fallbackReasons.Clear();
            _startTicks = Stopwatch.GetTimestamp();
            _monoBefore = GC.GetTotalMemory(false);
        }

        private static Exception StartFinalizer(Exception __exception)
        {
            if (_summaryDepth > 0) _summaryDepth--;
            if (_summaryDepth > 0) return __exception;
            try
            {
                int light = _partial + _fullThenLight;
                if (_disabledFull > 0)
                {
                    double ms0 = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} 設定が無効のため、招待メニューのカード {_disabledFull}枚を ksPlug の元の処理で完全に読みました（{State()}）。" +
                        $"所要 {ms0:F0}ms、Mono 使用 {_monoBefore / 1048576}→{GC.GetTotalMemory(false) / 1048576}MB。");
                }
                if (light > 0 || _loadFailed > 0)
                {
                    double ms = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
                    long monoAfter = GC.GetTotalMemory(false);
                    var reasons = new List<string>();
                    foreach (KeyValuePair<string, int> r in _fallbackReasons) reasons.Add($"{r.Key} {r.Value}枚");
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} 招待メニューのカード {light}枚を軽量版にしました（部分読み込み {_partial}枚 / 完全に読んでから軽量化 {_fullThenLight}枚" +
                        (reasons.Count > 0 ? $"（{string.Join("、", reasons.ToArray())}）" : "") +
                        $" / 読み込めず一覧に入らなかったもの {_loadFailed}枚）。所要 {ms:F0}ms、Mono 使用 {_monoBefore / 1048576}→{monoAfter / 1048576}MB。招待する瞬間に完全に読み直します（{State()}）。");
                }
            }
            catch { }
            return __exception;
        }

        // ===================== 1. 一覧のカードを軽量版にする =====================

        /// <summary>
        /// 原型 AddChaFileToList と同じ FileInfo を作って一覧に足す。違いは File が軽量版であることと、pngData を手放すことだけ。
        /// 原型: LoadCharaFile に成功したら FileInfo { File, png = ChangeTextureFromByte(pngData, 0.7*num, num), filter, cardPath } を足す（num = (Screen.height / 6) * 1.5）。
        /// </summary>
        private static bool AddChaFileToListPrefix(FolderAssist.FileInfo fileInfo, int sex, string folderFilter, object[] __args)
        {
            if (!_restoreReady || PatcherPlugin.IsQuitting) return true;
            if (!PatcherPlugin.EnableLazyInviteCardList.Value)
            {
                _disabledFull++; // 設定が無効なので原型どおり完全に読む（まとめのログで数を出す）
                return true;
            }
            try
            {
                string path = fileInfo != null ? fileInfo.FullPath : null;
                var list = __args[3] as IList;
                if (string.IsNullOrEmpty(path) || list == null) return true;

                ChaFileControl light = null;
                if (PatcherPlugin.LazyInvitePartialLoad.Value)
                {
                    var c = new ChaFileControl();
                    if (TryPartialLoad(c, path, (byte)sex, out string reason))
                    {
                        light = c;
                        _partial++;
                    }
                    else
                    {
                        _fallbackReasons.TryGetValue(reason, out int n);
                        _fallbackReasons[reason] = n + 1;
                    }
                }
                if (light == null)
                {
                    var full = new ChaFileControl();
                    if (!full.LoadCharaFile(path, (byte)sex, false, true))
                    {
                        _loadFailed++; // 原型も一覧に入れない
                        return false;
                    }
                    light = MakeLight(full);
                    if (light == null) return true; // 作れなければ原型に任せる（完全版のまま）
                    _fullThenLight++;
                }

                object info = Activator.CreateInstance(_fileInfoType);
                _fiFile.SetValue(info, light);
                int num = (int)((float)(Screen.height / 6) * 1.5f);
                _fiPng.SetValue(info, (Texture)PngAssist.ChangeTextureFromByte(light.pngData, (int)((float)num * 0.7f), num));
                _fiFilter.SetValue(info, folderFilter);
                _fiCardPath.SetValue(info, path);
                light.pngData = null; // 顔のテクスチャを作ったので手放す（招待のときは完全に読み直す）
                _light.Add(light, Mark);
                list.Add(info);
                return false;
            }
            catch (Exception ex)
            {
                LogOnce("add:" + ex.GetType().Name, $"{Tag} 一覧のカードの軽量化に失敗したため、このカードは元どおり完全に読みます（以降、同じ種類の失敗は出しません）: {ex}");
                return true;
            }
        }

        /// <summary>
        /// 全身画像・顔画像・Parameter ブロックだけを読む。KKS の ChaFile.LoadFile と同じ順に読む
        /// （全身画像 → productNo → 識別子 → バージョン → 顔画像 → BlockHeader → Parameter ブロックへシーク）。
        /// 読めなければ false と理由を返す（呼び出し側が完全な読み込みに回す）。
        /// </summary>
        private static bool TryPartialLoad(ChaFileControl c, string filename, byte sex, out string reason)
        {
            reason = null;
            try
            {
                _charaFileName.SetValue(c, Path.GetFileName(filename), null);
                string path = c.ConvertCharaFilePath(filename, sex);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    long pngSize = PngFile.GetPngSize(br);
                    if (pngSize != 0)
                    {
                        c.pngData = br.ReadBytes((int)pngSize);
                        if (br.BaseStream.Length - br.BaseStream.Position == 0) { reason = "PNG だけでキャラデータが無い"; return false; }
                    }
                    if (br.ReadInt32() > 100) { reason = "productNo が範囲外"; return false; }
                    if (br.ReadString() != KksCardMark) { reason = "キャラカードの識別子が違う"; return false; }
                    var version = new Version(br.ReadString());
                    if (ChaFileDefine.ChaFileVersion.CompareTo(version) < 0) { reason = "カードのバージョンが新しすぎる"; return false; }
                    c.loadVersion = version;

                    int faceSize = br.ReadInt32();
                    if (faceSize != 0) c.facePngData = br.ReadBytes(faceSize);

                    int headerSize = br.ReadInt32();
                    var header = MessagePack.MessagePackSerializer.Deserialize<BlockHeader>(br.ReadBytes(headerSize));
                    br.ReadInt64();
                    long dataStart = br.BaseStream.Position;

                    BlockHeader.Info info = header.SearchInfo(ChaFileParameter.BlockName);
                    if (info == null) { reason = "Parameter ブロックが無い"; return false; }
                    if (ChaFileDefine.ChaFileParameterVersion.CompareTo(new Version(info.version)) < 0) { reason = "Parameter のバージョンが新しすぎる"; return false; }
                    br.BaseStream.Seek(dataStart + info.pos, SeekOrigin.Begin);
                    c.SetParameterBytes(br.ReadBytes((int)info.size));
                }
                // 元の LoadCharaFile(BinaryReader) は CheckDataRange で範囲外の値を直す。ここでは Parameter の分だけ直す
                // （元は結果を Character.isMod に入れるが、一覧のカードはキャラを作らないので触らない）
                if (!c.skipRangeCheck) ChaFileControl.CheckDataRangeParameter(c);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name;
                return false;
            }
        }

        /// <summary>完全版から軽量版を作る（parameter・charaFileName・画像だけ。CopyAll は拡張データごと複製するので使わない）。</summary>
        private static ChaFileControl MakeLight(ChaFileControl full)
        {
            try
            {
                var light = new ChaFileControl();
                light.CopyParameter(full.parameter);
                light.pngData = full.pngData;
                light.facePngData = full.facePngData;
                light.loadVersion = full.loadVersion;
                _charaFileName.SetValue(light, full.charaFileName, null);
                return light;
            }
            catch (Exception ex)
            {
                LogOnce("light", $"{Tag} 軽量版の作成に失敗しました（このカードは完全版のまま扱います。以降は出しません）: {ex.Message}");
                return null;
            }
        }

        // ===================== 2. 招待の前に完全に読み直す =====================

        /// <summary>軽量版なら cardPath から完全に読み直して FileInfo.File を差し替える。設定を無効にした後も行う。</summary>
        private static void InviteSomeonePrefix(object chaFile)
        {
            try
            {
                var c = chaFile != null ? _fiFile.GetValue(chaFile) as ChaFileControl : null;
                if (c == null || !_light.TryGetValue(c, out _)) return;
                string path = _fiCardPath.GetValue(chaFile) as string;
                var full = new ChaFileControl();
                if (!string.IsNullOrEmpty(path) && full.LoadCharaFile(path, c.parameter.sex, false, true))
                {
                    _fiFile.SetValue(chaFile, full);
                    _light.Remove(c);
                    PatcherPlugin.Logger.LogInfo($"{Tag} 招待のためカードを読み直しました: {c.parameter.fullname} ({path})");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} 招待のためのカードの読み直しに失敗しました（ファイルが移動・削除された可能性）。軽量版のまま招待するため、見た目が既定の姿になります: {c.parameter.fullname} ({path})");
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 招待のためのカードの読み直しで例外が起きました: {ex}");
            }
        }

        private static void LogOnce(string key, string message)
        {
            if (_loggedOnce.Add(key)) PatcherPlugin.Logger.LogWarning(message);
        }
    }
}
