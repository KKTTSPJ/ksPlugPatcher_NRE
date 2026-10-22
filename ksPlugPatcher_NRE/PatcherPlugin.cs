using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// Koikatsu Sunshine（KKS）の ksPlug / ksPlugVR と、それに関係するゲーム本体の NullReferenceException への回避策を集めるプラグイン。
    ///
    /// KK の kPlug で kPlugPatcher_InviteChrID が対策した「衣装の読み直しなどで破棄されたオブジェクトを持ち続けて例外を出す」不具合が、
    /// KKS の ksPlug とゲーム本体にも同じ形で残っている（実機ログで確認）。
    /// - <see cref="ParentObjectCtrlStaleRefFix"/>: ゲーム本体の ParentObjectCtrl。絶頂の終わりの処理が止まり、絶頂が繰り返す（絶頂ループ）。
    /// - <see cref="KsPlugStaleRefFix"/>: ksPlug の ChaTools.ColDefsManager / BellyCtrl。毎フレーム例外を出し続ける。
    /// - <see cref="CtrlBonesStaleRefFix"/>: ksPlug の CtrlBones（ボーンの補正）と AI.IsCloseFromMBoy。毎フレーム例外を出し続ける。
    /// - <see cref="FaceListReloadOnSwap"/>: ksPlug の交代で H の表情のリストが読み直されない問題。
    /// - <see cref="MaterialNullGuard"/>: ksPlug が顔のマテリアルを確かめずに使う問題（表情の頬の色など）。
    /// - <see cref="LazyInviteCardList"/>: ksPlug の招待メニューが全カードを完全に読み込んで保持する問題（メモリ）。
    /// - <see cref="DynamicBonePerf"/>: 多人数の招待で揺れもの（DynamicBone）の負荷が増えて遅くなる問題の計測と回避策（NRE ではないが ksPlug に関係する）。
    /// - <see cref="HContextResetOnTitle"/>: 設定画面からタイトルに戻ると ksPlug の H の状態が残り、招待キャラが残る・次の H を終えられない問題。
    /// - <see cref="HStallDiag"/>: H が絶頂の前後で先に進まなくなる問題を調べる診断ログ（回避策ではない）。
    /// - <see cref="HoushiVoiceWaitFix"/>: ゲーム本体の奉仕で voiceWait が戻らず、H の UI が押せないまま止まる問題。
    /// - <see cref="VRMenuRaycastPriority"/>: ksPlugVR のメニューが、レイが当たっているのにタイトルなどのキャンバスに取られてクリックできない問題（VR のみ）。
    /// - <see cref="CharaLoadGCClearDiag"/>: H 中の着替えで使っていない画像の掃除が動かない原因を調べる診断ログ（回避策ではない）。
    /// - <see cref="HOutfitCleanup"/>: H 中の着替えで出た古い服の画像が H を出るまで解放されない問題（ゲーム本体の KKS の不具合）。
    ///
    /// 方針（kPlugPatcher_InviteChrID と同じ）:
    /// - 原型の挙動を変えない。破棄済みの要素を飛ばす・探し直す以外は、原型と同じ順序・同じ戻り値にする。
    /// - 例外を外へ出さない。回避策の側で想定外のことが起きたら原型の処理に委ねる。
    /// - 回避策ごとに設定で無効にできるようにする。
    /// </summary>
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("KoikatsuSunshine")]
    [BepInProcess("KoikatsuSunshine_VR")]
    // ksPlug / ksPlugVR より後に読み込む（どちらも無くても動く。ゲーム本体の回避策は ksPlug が無くても当てる）
    [BepInDependency("ksPlug.Katarsys", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("ksPlugVR.Katarsys", BepInDependency.DependencyFlags.SoftDependency)]
    public class PatcherPlugin : BaseUnityPlugin
    {
        public const string GUID = "kkttspj.kks.ksplugpatcher.nre";
        public const string PluginName = "ksPlug Patcher NRE";
        public const string Version = "0.10.2";

        /// <summary>0.5.1 までの GUID。設定ファイル（{GUID}.cfg）の引き継ぎにだけ使う。</summary>
        private const string LegacyGuid = "com.your.kks.ksplugpatcher.nre";

        internal static new ManualLogSource Logger;
        internal static PatcherPlugin Instance;
        internal static bool IsQuitting;

        internal static ConfigEntry<bool> EnableParentObjectCtrlStaleRefFix;
        internal static ConfigEntry<bool> EnableColDefsManagerStaleRefFix;
        internal static ConfigEntry<bool> EnableBellyCtrlStaleRefFix;
        internal static ConfigEntry<bool> EnableCtrlBonesStaleRefFix;
        internal static ConfigEntry<bool> EnablePerfDiagnostics;
        internal static ConfigEntry<bool> DisableGuestDynamicBones;
        internal static ConfigEntry<bool> DisableGuestBreastDynamicBones;
        internal static ConfigEntry<bool> LimitDynamicBoneIterations;
        internal static ConfigEntry<int> MaxDynamicBoneIterationsPerFrame;
        internal static ConfigEntry<bool> EnableDynamicBoneColliderCache;
        internal static ConfigEntry<bool> MergeDynamicBoneColliderLoop;
        internal static ConfigEntry<bool> VerifyDynamicBoneColliderCache;
        internal static ConfigEntry<bool> EnableLazyInviteCardList;
        internal static ConfigEntry<bool> LazyInvitePartialLoad;
        internal static ConfigEntry<bool> ShrinkInviteMenuFaces;
        internal static ConfigEntry<bool> FixVRMenuRay;
        internal static ConfigEntry<VRMenuRaycastPriority.PreferMode> VRMenuRaycastMode;
        internal static ConfigEntry<bool> LogVRMenuRaycastHits;
        internal static ConfigEntry<bool> EnableCharaLoadGCClearDiagnostics;
        internal static ConfigEntry<bool> EnableHOutfitCleanup;
        internal static ConfigEntry<HOutfitCleanup.CleanupTiming> HOutfitCleanupTiming;
        internal static ConfigEntry<float> HOutfitCleanupDelaySeconds;
        internal static ConfigEntry<float> HOutfitCleanupIdleFallbackSeconds;
        internal static ConfigEntry<bool> HOutfitCleanupBypassIllusionFixes;
        internal static ConfigEntry<bool> EnableFaceListReloadOnSwap;
        internal static ConfigEntry<bool> EnableMaterialNullGuard;
        internal static ConfigEntry<bool> EnableHContextResetOnTitle;
        internal static ConfigEntry<bool> EnableEndHGuard;
        internal static ConfigEntry<bool> EnableHStallDiagnostics;
        internal static ConfigEntry<int> HStallSeconds;
        internal static ConfigEntry<bool> EnableSpeedUpVoiceWaitFix;
        internal static ConfigEntry<bool> EnableVoiceWaitWatchdog;
        internal static ConfigEntry<float> VoiceWaitWatchdogSeconds;

        private Harmony _harmony;

        private void Awake()
        {
            Logger = base.Logger;
            Instance = this;
            Logger.LogInfo($"{PluginName} v{Version} を読み込みました。");
            // 0.5.1 までの GUID の設定ファイルを引き継ぐ。どの設定を読むよりも前に行う。
            MigrateLegacyConfigFile();

            EnableParentObjectCtrlStaleRefFix = Config.Bind(
                "ParentObjectCtrlStaleRefFix", "EnableParentObjectCtrlStaleRefFix", true,
                "H で、ゲージが上がっていないのに絶頂が繰り返される（絶頂ループ）問題の回避策を有効にするかどうか。" +
                "ゲーム本体の ParentObjectCtrl（H の小物の表示）が、衣装の読み直しなどで破棄された小物を確かめずに触って NullReferenceException を出し、" +
                "HSonyu.Proc の絶頂の終わりの処理（ゲージを 0 に戻す）が実行されないことが原因です。破棄済みの小物を飛ばす同じ処理に置き換えます。" +
                "※ ksPlug ではなくゲーム本体（Assembly-CSharp）へのパッチです。");
            EnableColDefsManagerStaleRefFix = Config.Bind(
                "KsPlugStaleRefFix", "EnableColDefsManagerStaleRefFix", true,
                "ksPlug の ChaTools.ColDefsManager（当たり判定の表示の切り替え）が、破棄されたオブジェクトを持ち続けて毎フレーム NullReferenceException を出す問題の回避策を有効にするかどうか。" +
                "破棄済みのオブジェクトを一覧から取り除きます。");
            EnableBellyCtrlStaleRefFix = Config.Bind(
                "KsPlugStaleRefFix", "EnableBellyCtrlStaleRefFix", true,
                "ksPlug の BellyCtrl（お腹の膨らみ）が、体の作り直しで破棄されたボーンを持ち続けて毎フレーム NullReferenceException を出す問題の回避策を有効にするかどうか。" +
                "破棄されたボーンを、同じ名前のボーンからキャラの中で探し直します。");
            EnableCtrlBonesStaleRefFix = Config.Bind(
                "KsPlugStaleRefFix", "EnableCtrlBonesStaleRefFix", true,
                "ksPlug の CtrlBones（体位ごとのボーンの補正。設定 OffsetOversized の体格差の補正など）と AI.IsCloseFromMBoy が、体の作り直しで破棄されたボーンを持ち続けて NullReferenceException を出す問題の回避策を有効にするかどうか。" +
                "背骨のボーンが破棄されていれば ksPlug 自身の CtrlBones.GetBones で探し直し、補正のボーンを付け替えます。");

            EnablePerfDiagnostics = Config.Bind(
                "DynamicBonePerf", "EnablePerfDiagnostics", false,
                "H の間、30秒ごとに、平均フレーム時間・キャラの数（ksPlug の招待キャラを含む）・揺れもの（DynamicBone）の数と処理時間・物理の回数をログに出すかどうか。" +
                "多人数の招待で遅くなる原因を調べるための計測です（揺れもの1つごとに時間を測るので、わずかに負荷が増えます）。");
            DisableGuestDynamicBones = Config.Bind(
                "DynamicBonePerf", "DisableGuestDynamicBones", true,
                "ksPlug の招待キャラの揺れもの（髪・服・アクセサリなどの DynamicBone）の計算を止めるかどうか。止めた揺れものは初期の姿勢で静止します。" +
                "メインのヒロインと男性は対象外。多人数の招待で遅くなる問題の回避策です（KKS の実機で、7人のとき1フレーム 137ms → 25ms。計算回数の上限と合わせた値）。ゲーム中に切り替えられます。");
            DisableGuestBreastDynamicBones = Config.Bind(
                "DynamicBonePerf", "DisableGuestBreastDynamicBones", false,
                "DisableGuestDynamicBones が有効なとき、招待キャラの胸など（DynamicBone_Ver02）の揺れも止めるかどうか。数が少なく負荷は小さいので、通常は止めなくてかまいません。");
            LimitDynamicBoneIterations = Config.Bind(
                "DynamicBonePerf", "LimitDynamicBoneIterations", true,
                "すべてのキャラの揺れもの（DynamicBone）の1フレームの計算回数を MaxIterationsPerFrame までに抑えるかどうか。" +
                "ゲーム本体はフレームが遅いと1フレームで最大3回計算するため、遅くなるほど計算が増える悪循環になります。60fps 以上なら見た目は変わりません。ゲーム中に切り替えられます。");
            MaxDynamicBoneIterationsPerFrame = Config.Bind(
                "DynamicBonePerf", "MaxIterationsPerFrame", 1,
                new ConfigDescription("LimitDynamicBoneIterations の上限（1〜2。3以上はゲーム本体と同じ）。", new AcceptableValueRange<int>(1, 3)));
            EnableDynamicBoneColliderCache = Config.Bind(
                "DynamicBoneColliderCache", "EnableColliderCache", true,
                "揺れもの（DynamicBone / Ver01 / Ver02）の当たり判定を、結果を変えずに軽くする（0.8.0）。ゲーム本体は、揺れものの粒ごとに全部のコライダーの位置と大きさを求め直しているため、粒とコライダーの多い髪・服で1つ数 ms/f かかります。" +
                "有効な場合は、揺れもの1つの計算の最初に、コライダーの位置と大きさを1回だけ求めておき、粒ごとにはその値を使います（計算の式は元と同じ。KK の kPlugPatcher では、元の処理と結果が同じことを確かめました）。" +
                "招待キャラの揺れものを止めない場合にも効きます。ゲーム中に切り替えられます。");
            MergeDynamicBoneColliderLoop = Config.Bind(
                "DynamicBoneColliderCache", "MergeColliderLoop", true,
                "EnableColliderCache が有効なとき、粒ごとのコライダーのループ全体を1回の呼び出しにまとめる（0.8.0）。1組ごとに呼び出しを差し替える形より、さらに短くなります。" +
                "コライダーを回る順番と条件は元のループと同じ。ゲーム中に切り替えられます。");
            VerifyDynamicBoneColliderCache = Config.Bind(
                "DynamicBoneColliderCache", "VerifyColliderCache", false,
                "確かめる用（0.8.0）。EnableColliderCache が有効なとき、前計算の結果と元の処理の結果を毎回比べ、違った回数と最大の差を60秒ごとのログに出します。" +
                "元の処理も呼ぶので、有効にしている間は元より重くなります。ゲーム中に切り替えられます。");

            EnableLazyInviteCardList = Config.Bind(
                "LazyInviteCardList", "EnableLazyInviteCardList", true,
                "ksPlug の招待メニューが、フリーH などで H に入るたびに全キャラカードを完全な形で読み込んで保持する問題（カードが多いと数GB）の回避策を有効にするかどうか。" +
                "一覧には名前・性格と顔画像だけの軽量版を置き、招待する瞬間にそのカードだけを完全に読み直します。ストーリー中の H（セーブのヒロインの一覧）は対象外です。");
            LazyInvitePartialLoad = Config.Bind(
                "LazyInviteCardList", "PartialLoad", true,
                "一覧のカードを、ファイルから名前・性格（Parameter）と画像だけを読む部分読み込みで作るかどうか。無効にすると、1枚ずつ完全に読んでから軽量版にします（拡張データの展開や見つからない MOD の警告はそのまま起きます）。");
            ShrinkInviteMenuFaces = Config.Bind(
                "LazyInviteCardList", "ShrinkInviteMenuFaces", true,
                "招待メニューの一覧の顔画像を、表示に足りる大きさの読み書き不可の画像にして作るかどうか（0.9.0）。ksPlug はカードの画像の大きさのまま・読み書き可能で作るため、" +
                "カードが多いと GPU と CPU の両方に数百MBずつ残ります（KKS で 471枚・GPU 486MB＋CPU 486MB、タイトルに戻っても残る）。" +
                "大きさの上限は、通常版は ksPlug の表示の大きさ、VR 版は 252x352。EnableLazyInviteCardList が有効なときだけ働きます。次に一覧を読むときから反映されます。");

            FixVRMenuRay = Config.Bind(
                "VRMenuRaycast", "FixRayForKsPlugMenus", true,
                "VR のみ（0.9.1）。タイトルなどで ksPlugVR のメニューがクリックできない問題の回避策。VRTK はキャンバスを1つずつ調べるとき、当たったキャンバスがレイを「当たった点・向き 0」に書き換えるため、" +
                "後に調べるキャンバスは必ず外れます（タイトルの UI のキャンバスの範囲では、手前の ksPlug のメニューに当たらない）。ksPlug のメニューを調べるときだけ元のレイに戻します。" +
                "ゲームのキャンバスどうしの調べ方は変えません。ゲーム中に切り替えられます。");
            VRMenuRaycastMode = Config.Bind(
                "VRMenuRaycast", "PreferKsPlugMenu", VRMenuRaycastPriority.PreferMode.Off,
                "VR のみ（0.9.0）。ksPlugVR のメニューにレイが当たっているのに、タイトルの UI などの他のキャンバスが先に選ばれてクリックできないときに、ksPlug のメニューを優先するかどうか。" +
                "Off: 何もしない（ゲームの元の順番、0.10.2 から既定）。WhenNearer: ksPlug のメニューの方が手前にあるときだけ優先する。Always: ksPlug のメニューに当たっていれば常に優先する。" +
                "タイトルの問題の原因はレイの書き換えで（FixRayForKsPlugMenus で直る）、並べ替えでは直らなかったため、既定は Off です。" +
                "ksPlug のメニューに当たっていないときは何もしません。ゲーム中に切り替えられます。");
            LogVRMenuRaycastHits = Config.Bind(
                "VRMenuRaycast", "LogRaycastHits", false,
                "VR のみ（0.9.0）。確かめる用（0.10.2 から既定 false）。レイの当たり先の先頭が変わったときに、上位4つ（キャンバス・sortingLayer・sortingOrder・距離・ksPlug のメニューか）をログに出すかどうか。" +
                "シーンごとに80行まで。ゲーム中に切り替えられます。");
            EnableCharaLoadGCClearDiagnostics = Config.Bind(
                "CharaLoadGCClearDiag", "EnableCharaLoadGCClearDiagnostics", false,
                "確かめる用（0.9.0、0.10.2 から既定 false）。H 中の着替えで使っていない画像の掃除（UnloadUnusedAssets）が動かない原因を調べるため、ゲーム本体の設定値 Manager.Character.enableCharaLoadGCClear が変わったときと、" +
                "着替え（ChangeCoordinateTypeAndReload）のときの値をログに出すかどうか。回避策ではありません。ゲーム中に切り替えられます。");
            EnableHOutfitCleanup = Config.Bind(
                "HOutfitCleanup", "EnableHOutfitCleanup", true,
                "H 中の着替えで出た古い服の画像（RenderTexture など）を、着替えの数秒後にまとめて1回解放するかどうか（0.10.0）。KKS のゲーム本体は、H でキャラを読み込むと着替えの後の掃除を止めたままにするため、" +
                "H を出るまで古い服の画像が残り、着替えを続けると VRAM が溢れることがあります（女6人で +5.6GB）。掃除の間（0.5〜1秒ほど）止まります（KK は着替えのたびに止まります）。" +
                "止まるのが気になる場合は無効にしてください（H を出れば解放されます）。ゲーム中に切り替えられます。");
            HOutfitCleanupTiming = Config.Bind(
                "HOutfitCleanup", "Timing", HOutfitCleanup.CleanupTiming.WithNextChange,
                "掃除する時期（0.10.1）。WithNextChange: 前の着替えから DelaySeconds 以上たってからの次の着替えの直後に、それまでの分を掃除する（既定。着替えの停止に続けて止まるので目立ちにくい。" +
                "最後の着替えの分は次の着替えか H を出るまで残る）。AfterDelay: 最後の着替えから DelaySeconds 後に掃除する（0.10.0 の動き。着替えの後に別に一瞬止まる）。ゲーム中に切り替えられます。");
            HOutfitCleanupDelaySeconds = Config.Bind(
                "HOutfitCleanup", "DelaySeconds", 5f,
                new ConfigDescription("WithNextChange: 前の着替えからこの秒数以上たっていれば、次の着替えのときに掃除する（続けて着替える間は掃除しない）。" +
                    "AfterDelay: 最後の着替えから掃除までの秒数（続けて着替えたら数え直す）。どちらも、何人続けて着替えても1回で済みます。", new AcceptableValueRange<float>(1f, 60f)));
            HOutfitCleanupIdleFallbackSeconds = Config.Bind(
                "HOutfitCleanup", "IdleFallbackSeconds", 0f,
                new ConfigDescription("WithNextChange のとき、着替えが無いままこの秒数たったら、残っている分を掃除する（0 で無効。既定）。最後の着替えの分を早く解放したい場合に使います（そのときは一瞬止まります）。",
                    new AcceptableValueRange<float>(0f, 600f)));
            HOutfitCleanupBypassIllusionFixes = Config.Bind(
                "HOutfitCleanup", "BypassIllusionFixesMemoryCheck", true,
                "IllusionFixes（ResourceUnloadOptimizations）が入っているとき、その「RAM の使用率が低ければ掃除を飛ばす」判定を通さずに掃除するかどうか。" +
                "IllusionFixes は RAM だけを見て VRAM を見ないため、RAM に余裕がある PC では掃除が飛ばされます。");

            EnableFaceListReloadOnSwap = Config.Bind(
                "FaceListReloadOnSwap", "EnableFaceListReloadOnSwap", true,
                "ksPlug のキャラ交代で、H の表情のリストも交代後のキャラの性格で読み直すかどうか。ksPlug は交代で声のリストだけを読み直すため、" +
                "交代後のキャラは H の開始時の性格の表情番号表で顔を作り、セリフと合わない顔になります。その性格の表情リストが無い場合は前のまま使います。");

            EnableMaterialNullGuard = Config.Bind(
                "MaterialNullGuard", "EnableMaterialNullGuard", true,
                "ksPlug が、顔（と体）のマテリアルが無いキャラ（特定の頭の MOD のカードなど）で、表情の頬の色や汁のマスクを変えようとして NullReferenceException を出す問題の回避策を有効にするかどうか。" +
                "マテリアルが無いときだけその変更を飛ばし、残りの処理は実行します（招待キャラの表情の反応、アヘ顔、NPC、汁）。変更はゲームの再起動後に反映されます。");

            EnableHContextResetOnTitle = Config.Bind(
                "HContextReset", "EnableHContextResetOnTitle", true,
                "ksPlug の H の途中で設定画面の「タイトルへ」を使うと、ksPlug が H の終わりを知らずに H の状態のまま残り、招待キャラがストーリーや次の H に残る問題の回避策を有効にするかどうか。" +
                "タイトルを読んだときに H の状態が残っていれば、ksPlug 自身の処理で H の外の状態に戻します（招待キャラは ksPlug が H の終わりと同じく片付けます）。通常版の ksPlug のみ（ksPlugVR は自分で戻します）。");
            EnableEndHGuard = Config.Bind(
                "HContextReset", "EnableEndHGuard", true,
                "ksPlug の H の終わりの処理（HManage.EndH）が、前の H の残りのキャラで例外になり、H を終えられなくなる問題の保険を有効にするかどうか。" +
                "前の H の残り（破棄済み）を一覧から取り除き、それでも例外になったら例外を止めて H を終えられるようにします。");
            EnableHStallDiagnostics = Config.Bind(
                "HStallDiag", "EnableHStallDiagnostics", false,
                "H が絶頂の前後（射精の前のループや声の待ち）で先に進まなくなる問題を調べるため、H の操作（クリック）と、StallSeconds 以上とどまったときの状態（声・シーンの重なり・ボタン・ksPlug の状態・マウスの下の UI）をログに出すかどうか。" +
                "回避策ではありません。通常版のみ。");
            HStallSeconds = Config.Bind(
                "HStallDiag", "StallSeconds", 15,
                new ConfigDescription("同じ状態にこの秒数とどまったら、状態をログに出します。", new AcceptableValueRange<int>(3, 600)));

            EnableSpeedUpVoiceWaitFix = Config.Bind(
                "HoushiVoiceWaitFix", "EnableSpeedUpVoiceWaitFix", true,
                "奉仕で、速さのパッドを押した瞬間と H の処理の切り替わりが重なると、ゲームが「声の終わりを待つ」状態（voiceWait）を立てたまま戻さず、" +
                "吹き出しのボタン・終了ボタン・体位のメニューが押せなくなる問題の回避策を有効にするかどうか。H が既に Idle から進んでいるときに立った voiceWait を戻します。" +
                "※ ksPlug ではなくゲーム本体へのパッチです。通常版のみ。");
            EnableVoiceWaitWatchdog = Config.Bind(
                "HoushiVoiceWaitFix", "EnableVoiceWaitWatchdog", true,
                "奉仕のループ中・射精を選ぶ前（WLoop・SLoop・OLoop）に、待つ声もクリックも無いのに voiceWait が WatchdogSeconds 続いたら戻すかどうか（上の回避策で防げない経路の保険）。" +
                "ゲームはこれらの状態で voiceWait を戻さないため、H の UI が押せないまま止まります。通常版のみ。");
            VoiceWaitWatchdogSeconds = Config.Bind(
                "HoushiVoiceWaitFix", "WatchdogSeconds", 3f,
                new ConfigDescription("EnableVoiceWaitWatchdog で戻すまでの秒数。", new AcceptableValueRange<float>(1f, 60f)));

            // 設定の変更をログに出す（F1 の設定画面での切り替えは BepInEx がログに出さないため。計測の前後を区別できるように）
            foreach (ConfigDefinition def in Config.Keys)
                WatchSetting(Config[def]);

            _harmony = new Harmony(GUID);
            ParentObjectCtrlStaleRefFix.TryApply(_harmony);
            FaceListReloadOnSwap.TryApply(_harmony);
            HStallDiag.TryApply(_harmony);
            HoushiVoiceWaitFix.TryApply(_harmony);
            CharaLoadGCClearDiag.TryApply(_harmony);
            HOutfitCleanup.TryApply(_harmony);
        }

        /// <summary>
        /// 0.5.1 までの GUID の設定ファイル（{LegacyGuid}.cfg）を、新しい GUID の名前に写して読み直す（0.6.0）。
        /// 新しいファイルが既にあれば何もしない（2回目以降の起動）。古いファイルは残す（旧版に戻したときのため）。
        /// </summary>
        private void MigrateLegacyConfigFile()
        {
            try
            {
                string newPath = Config.ConfigFilePath;
                string oldPath = Path.Combine(Path.GetDirectoryName(newPath), LegacyGuid + ".cfg");
                if (File.Exists(newPath) || !File.Exists(oldPath)) return;
                File.Copy(oldPath, newPath);
                Config.Reload();
                Logger.LogInfo($"[設定] {Path.GetFileName(oldPath)} を {Path.GetFileName(newPath)} に引き継ぎました（GUID の変更。古いファイルは残します）。");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[設定] 古い設定ファイル（{LegacyGuid}.cfg）を引き継げませんでした。既定値で続行します: {ex.Message}");
            }
        }

        private void Start()
        {
            // ksPlug の型は ksPlug の読み込み後に引く（BepInDependency で後に読み込まれるが、念のため Start で行う）
            KsPlugStaleRefFix.TryApply(_harmony);
            DynamicBonePerf.TryApply(_harmony);
            // 揺れものの当たり判定を、結果を変えずに軽くする（設定はゲーム中に切り替えられるので、常に当てる）
            DynamicBoneColliderCache.TryApply(_harmony);
            // ksPlugVR のメニューのレイの優先（VR のみ。ksPlugVR の VRUI の型を引くので Start で行う）
            VRMenuRaycastPriority.TryApply(_harmony);
        }

        private void FixedUpdate()
        {
            DynamicBonePerf.OnFixedUpdate();
        }

        private static readonly Dictionary<ConfigEntryBase, string> _lastSettingValues = new Dictionary<ConfigEntryBase, string>();

        /// <summary>設定が変わったら「[設定] [セクション] キー を 前 → 後 に変更しました（frame・時刻）」を出す。揺れものの設定なら計測の区切りも新しくする。</summary>
        private static void WatchSetting(ConfigEntryBase entry)
        {
            _lastSettingValues[entry] = Convert.ToString(entry.BoxedValue);
            EventHandler handler = (sender, args) =>
            {
                try
                {
                    string before = _lastSettingValues.TryGetValue(entry, out string b) ? b : "?";
                    string after = Convert.ToString(entry.BoxedValue);
                    if (before == after) return;
                    _lastSettingValues[entry] = after;
                    Logger.LogInfo($"[設定] [{entry.Definition.Section}] {entry.Definition.Key} を {before} → {after} に変更しました（frame {Time.frameCount}、{DateTime.Now:HH:mm:ss}）。");
                    if (entry.Definition.Section == "DynamicBonePerf") DynamicBonePerf.OnSettingChanged(entry.Definition.Key);
                }
                catch { }
            };
            // ConfigEntry<T>.SettingChanged は型ごとなのでリフレクションで登録する
            EventInfo ev = entry.GetType().GetEvent("SettingChanged");
            if (ev != null) ev.AddEventHandler(entry, handler);
        }

        /// <summary>OnApplicationQuit が呼ばれたフレーム。終了が取り消されたかの判定に使う。</summary>
        private int _quitRequestedFrame = -1;

        /// <summary>
        /// 終了確認で「いいえ」を選ぶと、ゲームは終了せずに続く（ゲーム本体が Application.CancelQuit で取り消す）。
        /// 次のフレームが来たら取り消されたとみなして戻す（kPlugPatcher_InviteChrID と同じ）。
        /// </summary>
        private void OnApplicationQuit()
        {
            IsQuitting = true;
            _quitRequestedFrame = Time.frameCount;
        }

        private void Update()
        {
            DynamicBonePerf.OnUpdate();
            CharaLoadGCClearDiag.OnUpdate();
            HOutfitCleanup.OnUpdate();
            if (IsQuitting && Time.frameCount > _quitRequestedFrame)
            {
                IsQuitting = false;
                _quitRequestedFrame = -1;
            }
        }

        /// <summary>
        /// 動作中のプロセスに合う ksPlug のアセンブリ（VR のプロセスなら ksPlugVR、そうでなければ ksPlug）。見つからなければ null。
        /// 通常版のプロセスでも ksPlugVR は読み込まれるが、VR の型を解決すると例外になるため触らない。
        /// </summary>
        internal static Assembly FindKsPlugAssembly(out string rootNamespace)
        {
            rootNamespace = Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "ksPlugVR" : "ksPlug";
            string name = rootNamespace;
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        }
    }
}
