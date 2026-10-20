# ksPlugPatcher_NRE

[English](README.md) / 日本語

Koikatsu Sunshine（KKS）用の BepInEx プラグインです。ksPlug / ksPlugVR と、それに関係するゲーム本体の不具合（主に NullReferenceException）を、外から回避します。
KK の kPlug に対する `kPlugPatcher_InviteChrID` の KKS・ksPlug 版にあたります（ksPlug は同じ名前のキャラを招待できる作りのため、招待の改名の機能はありません）。

- 対象: `KoikatsuSunshine` / `KoikatsuSunshine_VR`（CharaStudio は対象外）
- 対応する版: ksPlug・ksPlugVR 1.6.7〜1.7.3
- 実機で確認した版: ksPlug 1.7.0（通常版、効果の測定）、ksPlug 1.7.3（通常版）、ksPlugVR 1.7.3（パッチの適用と招待の一覧）。
- 版: 0.6.0
- ksPlug の作者向けの情報（英語）: [docs/KSPLUG_ISSUES.md](docs/KSPLUG_ISSUES.md)。不具合ごとの例外のスタック・原因・ksPlug の中での直し方の案をまとめています。

## 1. 解決する問題

| 問題 | 原因 | 回避策 |
|---|---|---|
| H で、ゲージが上がっていないのに絶頂が繰り返される（絶頂ループ） | ゲーム本体の `ParentObjectCtrl`（H の小物の表示）が、衣装の読み直しなどで破棄された小物を確かめずに触って例外を出し、絶頂の終わりの処理（ゲージを0に戻す）が実行されない | `Visible` / `VisibleObi` / `Proc` / `ProcObi` を、破棄済みの小物を飛ばす同じ処理に置き換える（**ゲーム本体へのパッチ**） |
| H の間、毎フレーム例外が出続ける | ksPlug の `ColDefsManager`（当たり判定の表示）・`BellyCtrl`（お腹の膨らみ）・`CtrlBones`（体位ごとのボーンの補正）が、体の作り直しで破棄されたオブジェクトやボーンを持ち続ける。お腹の膨らみ・ボーンの補正も止まる | 破棄済みを一覧から取り除く・同じ名前のボーンを探し直す（`CtrlBones` は ksPlug 自身のボーン探しを呼び直す） |
| フリーH などで、H に入るたびにメモリが数GB増える | ksPlug の招待メニューが、全キャラカードを拡張データまで完全に読み込み、H の間ずっと保持する（カード約330枚の環境で Mono +7GB 前後） | 一覧には名前・性格と顔画像だけの軽量版を置き（必要な部分だけを読む部分読み込み）、招待する瞬間にそのカードだけを完全に読み直す |
| 招待する人数が増えるほど重くなる | ゲーム本体の揺れもの（DynamicBone）の計算が招待キャラの分だけ増え、フレームが遅いと1フレームに最大3回計算するため悪循環になる | 招待キャラの揺れものの計算を止める・1フレームの計算回数を1回に抑える |
| ksPlug でキャラを交代すると、表情がセリフと合わない | ksPlug の交代は声のリストだけを読み直し、H の表情のリストが H の開始時の性格のまま残る | 声のリストの読み直しが終わったとき、表情のリストも同じ性格で読み直す |
| 一部のキャラで、表情が変わるたびに例外が出る | ksPlug が顔のマテリアルを確かめずに頬の色などを変える。顔のマテリアルが無いキャラ（特定の頭の MOD のカードなど）で例外になり、表情の処理の後半が飛ぶ | マテリアルが無いときだけその変更を飛ばし、残りの処理は実行する |

実機での効果（通常版、ksPlug 1.7.0）:
- 絶頂ループは発生しなくなり、ksPlug 由来の例外は0件になりました（対策前は1回の H で数千件）。
- フリーH の招待メニューの準備で増える Mono は、+7.6GB から +77〜117MB になりました。準備の時間は約2.4秒です。
- 1フレームの時間（女性7人）は、回避策なしで 137ms（7fps）、回避策ありで 25ms（40fps）でした。揺れものの処理は人数によらず約2msになります。

## 2. 導入方法

1. `ksPlugPatcher_NRE.dll` を `[ゲームのインストール先]\BepInEx\plugins\ksPlugPatcher_NRE\`（または任意のプラグインのフォルダ）にコピーします。
2. ゲームを起動します。

ksPlug が無い環境でも読み込めます。その場合、ゲーム本体への回避策（絶頂ループ、表情のリスト、揺れものの計算回数の上限）だけが働きます。

### 2.1. ソースからビルドする場合

本プロジェクトはゲーム本体と BepInEx の DLL を**相対パスで参照**します（`..\..\BepInEx\core\`、`..\..\KoikatsuSunshine_Data\Managed\`）。

1. `ksPlugPatcher_NRE` フォルダごと、**ゲームのインストール先の直下**に置きます。
2. そのフォルダで `build_and_deploy.ps1` を実行します（.NET SDK が必要です）。

`..\Deploy\ksPlugPatcher_NRE\` に出力されます。別の場所に置く場合は、`ksPlugPatcher_NRE.csproj` の `HintPath` と `GameManagedPath` を環境に合わせて書き換えてください。
ksPlug.dll は参照しません（ksPlug と ksPlugVR の型は、動作中のプロセスに合う方からリフレクションで取り出します）。

## 3. 設定項目（`BepInEx\config\kkttspj.kks.ksplugpatcher.nre.cfg`）

0.5.1 までの設定ファイル `com.your.kks.ksplugpatcher.nre.cfg` は、0.6.0 の初回の起動で新しい名前に引き継ぎます（古いファイルは残ります）。

| セクション | キー | 既定 | 内容 |
|---|---|---|---|
| ParentObjectCtrlStaleRefFix | EnableParentObjectCtrlStaleRefFix | true | 絶頂ループの回避策（ゲーム本体へのパッチ） |
| KsPlugStaleRefFix | EnableColDefsManagerStaleRefFix | true | ksPlug の ColDefsManager の例外の回避策 |
| KsPlugStaleRefFix | EnableBellyCtrlStaleRefFix | true | ksPlug の BellyCtrl の例外の回避策 |
| KsPlugStaleRefFix | EnableCtrlBonesStaleRefFix | true | ksPlug の CtrlBones・AI.IsCloseFromMBoy の例外の回避策 |
| LazyInviteCardList | EnableLazyInviteCardList | true | 招待メニューのカードの遅延読み込み。ストーリー中の H（セーブのヒロインの一覧）は対象外 |
| LazyInviteCardList | PartialLoad | true | 一覧のカードを部分読み込みで作る（無効にすると、1枚ずつ完全に読んでから軽量版にする） |
| FaceListReloadOnSwap | EnableFaceListReloadOnSwap | true | 交代時に表情のリストも読み直す（交代のたびに約0.5秒の読み込みが増えます） |
| MaterialNullGuard | EnableMaterialNullGuard | true | 顔のマテリアルが無いキャラの例外の回避策（再起動で反映） |
| DynamicBonePerf | DisableGuestDynamicBones | true | 招待キャラの揺れもの（髪・服・アクセサリ）の計算を止める。止めた揺れものは初期の姿勢で静止します。メインのヒロインと男性は対象外。ゲーム中に切り替えられます |
| DynamicBonePerf | DisableGuestBreastDynamicBones | false | 上に招待キャラの胸など（DynamicBone_Ver02）も含める |
| DynamicBonePerf | LimitDynamicBoneIterations | true | すべてのキャラの揺れものの1フレームの計算回数を MaxIterationsPerFrame までに抑える。60fps 以上なら見た目は変わりません |
| DynamicBonePerf | MaxIterationsPerFrame | 1 | 上の上限（1〜2。3以上はゲーム本体と同じ） |
| DynamicBonePerf | EnablePerfDiagnostics | false | H の間、30秒ごとにフレーム時間・揺れものの数と処理時間などをログに出す（調査用） |

設定を変更すると、ログに `[設定] … を 前 → 後 に変更しました` が出ます。

## 4. ログ

起動時に、回避策ごとに `… に回避策を適用しました` が出ます。ksPlug の版が違うなどで対象が見つからない場合は、その回避策だけが無効になり、Warning で理由が出ます。
回避策が働いたときの例:

```
[ParentObjectCtrlFix][ProcObi] … で、破棄済み・未登録の小物 7件を飛ばしました（anim=Insert）。
[ksPlugStaleRefFix][BellyCtrl] chaF_007 の破棄されたお腹のボーン … を探し直しました。
[LazyInviteCards] 招待メニューのカード 375枚を軽量版にしました（部分読み込み 375枚 / …）。所要 2365ms、Mono 使用 663→740MB。…
[LazyInviteCards] 招待のためカードを読み直しました: 名前 (パス)
[FaceListReload] 枠0: 表情リストを c21 → c08 に読み直しました（1回目、1457件、544ms）。
[MaterialNullGuard] FaceExpr.SetExpression: 名前 のマテリアルが無いため SetColor(_overcolor2) を飛ばしました（このキャラ・この所で 1回目）。
```

## 5. 既知の制約

- VR（ksPlugVR）は、ksPlugVR 1.7.3 でパッチの適用と招待の一覧の遅延読み込みをログで確認しています。VR の H の中で回避策が働いた記録はまだありません。
- VR の H には `HSceneProc` が無いため、`EnablePerfDiagnostics`（調査用の計測）のログは VR では出ません。回避策は VR でも働きます。
- 招待キャラの揺れものを止めると、招待キャラの髪・スカートなどは静止します（体の揺れは残ります）。気になる場合は `DisableGuestDynamicBones` を false にしてください（`LimitDynamicBoneIterations` だけでも、7人で 137ms → 42ms 程度に軽くなります）。
- 遅延読み込みで、一覧のカードは ChaFile の読み込みを通らないため、他のプラグインの読み込みの処理（Sideloader の「見つからない MOD」の警告など）は一覧のカードでは走りません。招待したカードは完全に読み直すので、そのキャラには通常どおり走ります。
- 顔のマテリアルの回避策は、実機で例外が出ていたことは確認していますが、回避策が実際に飛ばす動作はまだ確認できていません（回避策を入れた後は、まだ該当するキャラを招待していません）。
- Material Editor のテクスチャが H を出た後も残る問題は、本プラグインではなく、別のプラグイン KKS_METextureLeakFix で回避します。

## 6. 更新履歴

- 0.6.0: GitHub での公開に合わせて、GUID を `kkttspj.kks.ksplugpatcher.nre` に変えました（設定ファイルは初回の起動で引き継ぎます）。ライセンスを MIT にしました。ksPlug の作者向けの文書 `docs/KSPLUG_ISSUES.md` を加えました。ksPlug・ksPlugVR 1.7.3 での動作を確認しました。
- 0.5.1: ksPlugVR 1.7.2 で、遅延読み込みのまとめのログ（`[LazyInviteCards] 招待メニューのカード N枚を軽量版にしました …`）が出なかったのを直しました。ksPlugVR 1.7.2 は招待の一覧を H のキャラ選択の時点（`MenuInvite.InitFileList`）で読むため、ログもその時点で出ます。遅延読み込み自体は 0.5.0 でも働いていました。
- 0.5.0: 最初のリリース（ZIP での仮リリース）。

## 7. ライセンス・免責

- **MIT ライセンス**です（[LICENSE](LICENSE)）。本プラグインは、ksPlug の作者の負担を減らすため、見つかった不具合を実際に直す形で示すものです。ksPlug の作者は、コードや文書のどの部分も自由に ksPlug に取り込めます。
- **本ソフトは無保証で提供されます。** 利用によって生じたいかなる損害（セーブデータの破損、ゲームの不具合、他プラグインとの競合などを含む）についても、作者は責任を負いません。自己責任でご利用ください。
- **ksPlug は別の作者による著作物であり、本リポジトリ・配布物には含まれていません。** 本プラグインは ksPlug とゲーム本体の不具合を外から回避するためのパッチであり、ksPlug 本体を配布・改変するものではありません。ksPlug 本体は配布元から入手してください。ゲーム本体・BepInEx・その他のプラグインのファイルも同梱していません。
- 本プラグインは ksPlug とゲーム本体の動作を実行時に書き換えます。**ksPlug やゲームのバージョンが変わると対象のメソッドが見つからず、機能の一部または全部が無効になることがあります**（その場合はログに理由が出ます）。

---
作成日時: 2026年10月1日 / 最終更新: 2026年10月20日 (v0.6.0)
作成者: Claude Opus 5.5
