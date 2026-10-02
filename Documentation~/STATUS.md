# 全仕様の実装状態 — v0.1 G0/G1 checkpoint（Unity 実行済み）

基準: Unity_Texture_Paint_Spec_v0_1（22ページ、2026-10-02）。順序は実装順であり、未実装項目を最終スコープから除外するものではありません。

**重要:** 「実装」はコードがあるという意味です。2026-10-02 に Unity 2022.3.22f1（devcontainer）で初めてコンパイル・EditMode テスト・GPU 整合・ウィンドウ操作を実行しました（VALIDATION.md）。GPU はホストの RTX 3070 を OpenGL 4.6（Mesa d3d12 経由）で使っています。Windows の D3D11、実ペンタブ、Photoshop/CSP 実機は未実施で、G0/G1 の合格条件はまだ満たしていません。Unity 実行、コンテナの GPU（OpenGL）、Windows の D3D11、Photoshop/CSP 実機を区別します。

| 仕様 | 今回の現物 | 検証 | 残り / ゲート |
|---|---|---|---|
| 1 制作フロー・専用画面 | EditorWindow、2D/3D、ブラシ/レイヤー/保存UI、隔離スクリプト非生成プレビュー、デモキューブ。プロジェクトごとの設定（Project Settings > YoluPainter。共有: 既定の大きさ・共有のブラシ置き場。個人: ブラシ置き場・同梱ブラシの表示・.ylp のバックアップ数・復旧の間隔・メモリ予算）。ツール: ブラシ・バケツ・グラデーション・矩形/楕円/投げ縄選択・マジックワンド（2D キャンバス）、選択範囲の表示と全選択/解除/反転 | Unity で WindowTests（GUI、入力・取消・Undo・3D 描画・複数ウィンドウ・後片付け・元アセット非変更・.ylp の保存/開く/バックアップ/外部改変・PSD の取り込みと書き出し・Export Images・マスク・ブラシの取り込み/保存復元・設定・グループ・ツールと選択範囲）、PainterSettingsTests、プレビュー所有権 | 実Unity UI（GUI モードのエディタ画面はコンテナではマゼンタで見た目を確認できない）、操作復帰、UV診断の拡大、マルチTexture Set、3D ビューでの選択/バケツ、シーン上のプレビュー（NDMF、保留） |
| 2 ブラシ・入力 | 丸/画像の筆先（複数の筆先をランダム/順番に）、硬さ/間隔/角度/丸さ/進行方向、ストローク内の濃さ（流量で溜まり不透明度×筆圧が天井）、サイズ・角度・丸さ・不透明度・流量のゆらぎ、散布/数、紙の質感、サイズ・濃度・流量の圧力割当、UI圧力カーブ/JSONプリセット（schema 2）、2Dズーム/パン。選択範囲（矩形・楕円・投げ縄・マジックワンド、追加/削除/交差・反転、部分選択は結果を選択量で混ぜる）、塗りつぶし（バケツ・選択範囲の塗りつぶし・マスクの塗りつぶし）、グラデーション（線形/放射、プリマルチプライドの補間）、変形（Move ツール: ドラッグ・矢印キーで整数画素の移動、数値で回転・拡大縮小・オフセット、反転と ±90°。全チャンネルとマスクを一緒に、選択範囲があればその画素だけを持ち上げて選択範囲も動かす。1 回の Undo。1 画素に重なる移動・90° 回転・反転は透明画素の RGB まで含めてそのまま写し、それ以外はプリマルチプライドのバイリニアか最近傍）。内蔵 13 種、同梱 Krita 4 既定の筆先 76 種（CC0）、取り込み: GIMP .gbr/.gih/.vbr、Photoshop .abr（v1/2、v6+ の筆先と設定）、PNG。取り込んだブラシはプロジェクトの UserSettings に保存し、対応しない設定はブラシごとに知らせる | Unity EditMode（BrushTests・GimpBrushTests・PhotoshopBrushTests・BrushLibraryTests・RegionToolTests・TransformTests、ウィンドウからの取り込みとツール操作・移動ツール）。.abr はテスト内で組み立てたファイルだけで、Photoshop 実機で作ったファイルは未確認 | 実ペン、補正/入り抜き、混色、水彩、デュアルブラシ、カラーダイナミクス、パターン（.abr の patt）、筆圧以外のコントロール（フェード・傾き等）、CLIP STUDIO .sut（筆先が保護されたコンテナ内のため未対応）、Krita .kpp、定規、自由変形のハンドル（ドラッグでの回転・拡大縮小）とドラッグ中の中身のプレビュー（今は枠だけ）、ゆがみ/遠近、グループや Fill・調整層（マスクだけ）の変形、3D ビューでの移動、キャンバス回転、選択範囲のぼかし/拡張/縮小、選択範囲の保存 |
| 3 レイヤー | 追加（選択中の層のすぐ上）/削除/移動/名前/可視性/不透明度、合成モード 26 種（Photoshop の分離モード＋色相/彩度/カラー/輝度、ディザ以外）、構造Undo（スライダー操作は 1 つにまとめる）、ラスターマスク（隠す/見せる・有効・反転・濃度、全チャンネル共有）、Fill レイヤー、調整レイヤー（反転・レベル補正・色相/彩度/明度、アルファは変えない）、クリッピング、グループ（通過/分離、入れ子、グループのマスク・クリッピング、作成・解除・中身ごと削除・出し入れ） | Unity EditMode（MaskTests・FillTests・AdjustmentTests・ClippingTests・BlendModeTests・GroupTests・ウィンドウからの操作）、GPU 合成は全モード × 通常/クリッピング/調整と入れ子のグループで CPU と誤差 1 以内（GpuTests・GpuGroupTests） | 複製/統合、マスクのぼかし/表示/選択から作成、各ロック、トーンカーブ等の調整、ディザ、CLIP STUDIO 固有の合成（加算(発光)等）、複数選択、コピー貼付、ドラッグでの並べ替え、Photoshop 実機との描画の一致 |
| 4 マルチチャンネル/lilToon | Color/Roughness/Metallic/Height/Normal/Emissionの分離ソース、選択チャンネル表示/描画、PNG/PSD出力、Export Images（全チャンネルを PNG、Assets の中の新しいテクスチャにカラースペースを設定）。lilToon の読み取り専用アダプター（実際のパッケージのバージョン・バリアント・パイプライン・プロパティ・コンパイル済みの機能を確かめ、2.3.4 の Built-in の 32 バリアントでチャンネルとの対応を返す。未検証は断る） | 純C#分離、PSD橋渡し、LilToonAdapterTests（偽物のシェーダー・バージョン/パイプラインの拒否・マテリアルを変えないこと） | 同時複数チャンネルの一筆、チャンネル別設定、専用Normal合成、Height派生、lilToon への割り当て（ユーザーの確認を挟む。Roughness は _SmoothnessTex に反転、lilToon がコンパイルから外した機能は効かない）、URP/HDRP・Fur・Gem 等の検証。プレビューは今も中立シェーダー |
| 5 手続き効果 | Fill レイヤー（チャンネルごとの一定値を正本に、タイルをその場で生成。マスクと組み合わせ）、型付きノード/依存検証の基盤 | Fill の合成・Undo・変更追跡・保存、DAG型・順序テスト | 画像/手続き入力の Fill と UV 変換、mask/content効果スタック、Filter実行、Generator実行、プリセット/版/シード |
| 6 Anchor/mesh map | 同Texture Set・下位/先行・型・semantic・循環・移動・削除検証 | 純C#回帰 | 実評価段階、Anchor revision伝播、ベイク、mesh map来歴、stale、欠落解決、キャッシュ |
| 7 PSD互換 | RGB8 ラスターの入出力に、描画モード 26 種・クリッピング・ラスターマスク・非表示・不透明度・グループ（通過/分離、入れ子）・調整レイヤー（反転・レベル補正・色相/彩度。PSD の刻みに乗る値だけ書き、間の値は丸めずに断る。Photoshop の色相/彩度との式の違いは CompositeDiffers で知らせる）。描画に関係しない既知のメタデータ（lnsr・shmd・ロック・ガイド・XMP・sRGB の ICC など）は取り込んで「書き出しに含まれない」と一覧で知らせる。描画を変え得る未対応情報は PreserveOnly。原本のバイト列は .ylp に保持。書き出しは明示の Export PSD（Fill・刻みに乗らない調整・反転マスク・クリッピングされたグループは断る） | 実C# + 手組みの raw/RLE fixture、PsdTests・PsdLayerFeatureTests・PsdGroupTests・PsdMetadataTests・PsdAdjustmentTests（変異入力を含む）、psd-tools 1.23.0 で書き出したファイルの構造・調整レイヤーの種類と値の一致を確認（リポジトリ外） | Photoshop/CSP 実機での描画の一致、トーンカーブ等その他の調整・チャンネル別のレベル補正・範囲指定や色彩の統一の色相/彩度、Fill レイヤー、レイヤー効果、スマートオブジェクト、テキスト、高 bit、RLE での書き出し、新しい未分類ブロック（cinf・extn 等） |
| 8 PSD保持と再現 | 未知ブロック等の編集拒否、ガード付き書戻し、merged整合検査、RGBA/Unicode/IDs | 純C#/外部decoder、最大合成差1byteのfixture | 未編集ブロック単位パススルー・部分編集、高bit、色管理、検証済ネイティブ調整、Photoshop/CSP6工程 |
| 9 データモデル | UUID、schema（ネイティブ版 6: レイヤー種類・Fill 値・マスク・調整・クリッピング・グループ（親 ID）。版 1〜5 も読める）、channel、layer、tile archive。view/brush補助JSON。チャンネル別PSD | 独自保存往復byte一致、版 1〜5 の読み込み、入れ子の不正の拒否 | path/model binding指紋、procedural graph/資産保存、複数Texture Set、複雑な正本選択 |
| 10 保存・外部変更 | 1 ファイル形式 .ylp（zip。先頭に無圧縮の mimetype、全エントリーの SHA-256 と CRC、予算、新しい版の拒否）。保存はメモリで作って検証 → 協調ロック → 印の確認 → 一時ファイル（~ 付き）に書いて Flush・再検証 → 再確認 → 最後に 1 回だけ置換。直前の版を <名前>.ylp-backups~ に退避（保持数は設定、既定はすべて）。外部改変は印（長さ・時刻、違えば SHA-256）で検出して通常保存を拒否。Assets の中の .ylp はインポーターがチャンネルごとのテクスチャを出す。復旧 checkpoint は世代/manifest/current最後 | YlpArchiveTests（66、別のエージェントによる敵対的テスト）・YlpStoreTests（40、中断点・保持数・改名したバックアップを消さない等）・YlpImporterTests・ウィンドウの保存まわり（GUI） | OS電源断fsync、低ディスク/プロセス間ロック、非同期UI保存、debounceウォッチ、差分表示と競合解決Undo。世代フォルダ形式のプロジェクトはウィンドウから開けない（未リリースのため移行なし） |
| 11 Unity責務 | CPU純C#ソース + GPUタイル合成（約 512px のブロック単位。ブロックごとの署名で変わらないブロックは合成しない、最初に変わった層の下の合成結果の写しから合成し直す、層・マスクのブロックは書き換え番号が同じ間 GPU に残す。写しの予算は設定（自動 = VRAM の 1/8）で、2 分描かなければ手放す。入れ子のグループは深さごとの作業タイル、最大 8 段・32 MiB）、PreviewRenderUtility、リロード/閉じる処理、壊れたシェーダーでの CPU フォールバック | Unity で全アセンブリのコンパイル警告 0、GPU ブラシ誤差 0・GPU 合成誤差 1 以内（RTX 3070 / OpenGL 4.6。合成モードとグループは RTX 3070 のみ、llvmpipe は以前の範囲）、8 bit への丸めを CPU と同じ半分切り上げに揃えた、CopyTexture の無い GPU 向けの描き込み経路、ブロック合成と全面の合成のバイト一致（GpuInteractionTests、RTX 4080 SUPER）、4096²・全面 8 層で不透明度 1 回 7〜42 ms（古い合成器は 506〜527 ms） | D3D11 での整合、Jobs/Burst、AsyncGPUReadbackによるGPU正本、正確な色表示/各API |
| 12 表面・UV | CPU triangle ray、隣接seam、material、連結、表向き/可視性、重複pixel合併、過予算全拒否 | 同じ実装を数学アダプターで実行、細小三角形/近接衣服回帰 | 実画面、BVH/JOB高速化、padding、dilation、3D対称、重複UV制作UI、複雑な非多様体実物 |
| 13 ポーズ/3Dパス | 静的不変snapshot世代の判定。skinnedを除外した不完全モデルは塗れない | 幾何の世代・所有権試験 | ポーズ、BlendShape、BakeMesh、BVH refit、編集可能surface path、再結合、Filled/Ribbon |
| 14 色と精度 | straight RGBA8/隠れRGB保持、データ分離、CPU/GPU ping-pong、encoded-space明記 | 純C#byteテスト、PSDレイヤー一致 | ICC、線形/ガンマ実機表示、16/32bit、HDR、CMYK/Lab、専用法線合成/packing、PSD完全合成 |
| 15 4K/メモリ | 疎/一様タイル、source/rollback/history予算（既定は自動: 物理メモリから決める。16 GB で Undo 1 GiB・画素 2 GiB・1 回の操作 512 MiB。Project Settings の個人設定で数値に変えられ、開いているウィンドウにも反映。今の画素が予算を超えるドキュメントは画素を捨てず予算を広げて知らせる）、layer全面GPU非常駐、PSD/staging上限 | core予算atomic拒否/undo、自動の予算の値（4〜128 GB）と明示値の維持、16 GB の自動の値で 4K 全面の移動 8 回（直近 7 回が残り全部戻せる）、4K sparseケース、4K 4 レイヤー全面でドラッグ中の表示更新 中央値 1.2ms（RTX 3070） | 実RSS/VRAM、disk spill、LRU、低精度preview明示、codec streaming、4K dense大量レイヤー性能 |
| 16 scheduler/履歴 | 確定stroke単位のexact tile Undo/Redo、構造history、取消、予算通知、Undo の最小段数（既定 5。GIMP と同じく、予算を超えても直近の段は残す）、表示用のタイル変更追跡（ChangeSerial / TryGetChangedTiles） | core回帰・結合試験、変更追跡と差分合成の参照一致 | グループの中の境目での下の写し（今は最上位の境目だけ）、CPU フォールバックの高速化（Jobs/Burst が候補）、2048² の入れ子グループの全タイル再合成は GPU で約 0.22 秒、effect 用の依存 scheduler、halo、global filters、disk history、checkpoint pruning、永続操作ジャーナル |
| 17 実測計画・性能 | 実行環境とテスト結果を分離して記録 | 取得可能なcore実行のみ | 仕様の応答/メモリ/保存workloadの実Unity計測。推測値を合格値にしない |
| 18 受入・故障 | 破損入力/未知PSD/予算超過/中断/改変拒否、元asset保護 | Unity の EditMode で全件、元アセット非変更をウィンドウ経由で確認 | 実device、driver、Play/Reload/複数Window/source scene、ディスク/permission/lock/電源断 |
| 19 gate | 機能するcoreとcodecの検証結果、結合Editorプロトタイプ | Unity 実行で G1 の「元アセット非変更」「保存中断試験」は通過 | G0/G1未合格。実 GPU / Windows、ペンタブ、4K の実メモリ、実外部アプリの確認後に判断 |
| 20-21 参考 | 既存仕様調査の一次資料を継承、追加の検証元を文書化 | 出典リンク | 参照版/実動作保証を混同しない |

## コードがあるがまだUIから使えないもの

- Typed DependencyGraph: 純C#接続/移動/削除検証のみ。制作グラフやGeneratorの完成UIではない
- OrderedBrush shader: 明示GPU試験コマンドのみ。CPU正本の置換前にUnity画素誤差/寿命/Undo/save同期が必要
- lilToon アダプター（LilToonAdapter.Inspect）: 読み取り専用で、まだウィンドウに表示も割り当てもしていない

## 中断時点（2026-10-02、別の PC へ移るため一時停止）

コミット済みの `0.0.0` は両モードで全件検証済み（555 件、失敗 0。VALIDATION.md）。作業中だったものは
`wip/2026-10-02-pause` ブランチに 1 コミットでまとめて push してある（未検証・途中を含む。
そのまま `0.0.0` に入れず、内容を確かめてから分けて取り込む）。残り作業の詳細はそのブランチの `WIP~/README.md`。

- ~~PSD の調整レイヤー（レベル補正・色相/彩度・反転）の読み書き、クリッピングされたグループの書き出し拒否~~:
  新しい PC でテストの誤り 5 件を直し、文書と psd-tools の照合を済ませて 0.0.0 に取り込んだ
- ~~構造変更（スライダー操作など）の高速化~~: 新しい PC で速度を測り、書き換え番号を SparseTileSurface に移して
  （SurfaceChangeTracker は消した）、写しの予算を設定に出して 0.0.0 に取り込んだ。wip ブランチの中身はすべて取り込み済み
- ~~変形（移動・回転・拡大縮小）~~: 新しい PC で Core を見直し、テストとウィンドウの UI を足して 0.0.0 に取り込んだ。
  wip ブランチに残っているのは上の 2 つ（WIP~/README.md の 1 と 2）

再開の手順（新しい PC）:
1. `git clone https://github.com/YozoraKurage/YoluPainter.git` → `git switch 0.0.0`、devcontainer を開く
2. Unity のライセンスを有効化（`.devcontainer/unity/activate-license.sh --status`）、`test-daemon.sh start --batch-gl`
3. `git switch -c resume wip/2026-10-02-pause` などで作業中の分を取り出し、上の 3 つをそれぞれ確かめて仕上げる
4. 複数のエージェントで並行するときは `switch-daemon.sh` でモードを切り替える（TESTING.md）

この PC だけにあったもの（リポジトリには無い）:
- Claude のメモリ（Unity の用語はカタカナで書く／サブスタンス由来の機能を忘れない／GPU の同梱 Mesa の確認）
  → 要点はこの STATUS と CLAUDE.md にある
- サブエージェントの定義 `~/.claude/agents/yolupainter-worker.md`（Opus・思考量 xhigh）。新しい PC では作り直す

## 次の実装順

ユーザーと合意した順（2026-10-02）。未対応要望はスコープから外していない。

1. ユーザーの実環境（Windows の Unity・D3D11、実 GPU、ペンタブ）での G0 確認、Photoshop/CLIP STUDIO で書き出した PSD を開いて描画を確かめる、4K の実メモリ計測
2. ~~PSD の調整レイヤー（レベル補正・色相/彩度・反転）の読み書き~~（済）
3. ~~構造変更（スライダー操作・並べ替え）の高速化~~（済。4K・8 層で 1 回 7〜42 ms）
4. 描画ツールの残り（変形は済み。自由変形のハンドル、選択範囲のぼかし/拡張/縮小、3D ビューでの選択）
5. ノーマルマップ専用の合成、Height からノーマルマップ
6. lilToon への割り当て（ユーザーの確認を挟む、版固定）
7. サブスタンス由来の機能（ユーザーの依頼で忘れない）: mesh map のベイク（AO・曲率・位置・ノーマル・厚み等）、Generator（エッジの摩耗・汚れなど mesh map 駆動）、Filter、Anchor、永続 DAG
8. skinned preview/pose/BlendShape、model fingerprint、編集可能surface pathと再結合

対象Unity 2022.3はユーザー確認済みです。正確なpatch、OS、ペンタブ、lilToon版の確認でテスト対象をさらに絞れます。未対応要望を削除していません。
