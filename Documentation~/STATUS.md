# 全仕様の実装状態 — v0.1 G0/G1 checkpoint（Unity 実行済み）

基準: Unity_Texture_Paint_Spec_v0_1（22ページ、2026-10-02）。順序は実装順であり、未実装項目を最終スコープから除外するものではありません。

**重要:** 「実装」はコードがあるという意味です。2026-10-02 に Unity 2022.3.22f1（devcontainer、llvmpipe の OpenGL）で初めてコンパイル・EditMode テスト・GPU 整合・ウィンドウ操作を実行しました（VALIDATION.md）。実 GPU / D3D11、実ペンタブ、Photoshop/CSP 実機は未実施で、G0/G1 の合格条件はまだ満たしていません。Unity 実行、llvmpipe での GPU、実 GPU、Photoshop/CSP 実機を区別します。

| 仕様 | 今回の現物 | 検証 | 残り / ゲート |
|---|---|---|---|
| 1 制作フロー・専用画面 | EditorWindow、2D/3D、ブラシ/レイヤー/保存UI、隔離スクリプト非生成プレビュー、デモキューブ | Unity で WindowTests 12 件（入力・取消・Undo・3D 描画・複数ウィンドウ・後片付け・元アセット非変更）、プレビュー所有権 3 件 | 実Unity UI、操作復帰、UV診断の拡大、マルチTexture Set |
| 2 ブラシ・入力 | 円形/硬さ/間隔/Opacity/Flow/消去、サイズ・濃度・流量の圧力割当、UI圧力カーブ/JSONプリセット、2Dズーム/パン | 純C#サンプリング・圧力・Undo、入力UI未実行 | 実ペン、補正/入り抜き、画像筆先、混色、水彩、散布、定規、選択、塗潰し、勾配、変形、キャンバス回転 |
| 3 レイヤー | 追加/削除/移動/名前/可視性/不透明度、Normal/Multiply/Screen、構造Undo | 純C#実行 | グループ、複製/統合、マスク、クリッピング、各ロック、調整、複数選択、コピー貼付、PSD固有演算 |
| 4 マルチチャンネル/lilToon | Color/Roughness/Metallic/Height/Normal/Emissionの分離ソース、選択チャンネル表示/描画、PNG/PSD出力 | 純C#分離、PSD橋渡し | 同時複数チャンネルの一筆、チャンネル別設定、専用Normal合成、Height派生、lilToon版固定/アダプター/適用。現在は中立シェーダー |
| 5 手続き効果 | 型付きノード/依存検証の基盤のみ | DAG型・順序テスト | Fill、mask/content効果スタック、Filter実行、Generator実行、プリセット/版/シード、UI・保存の全実装 |
| 6 Anchor/mesh map | 同Texture Set・下位/先行・型・semantic・循環・移動・削除検証 | 純C#回帰 | 実評価段階、Anchor revision伝播、ベイク、mesh map来歴、stale、欠落解決、キャッシュ |
| 7 PSD互換 | RGB8 normalラスタ限定の入出力。未知情報はPreserveOnlyへ。全原本バイト保護 | 実C# + independent raw/RLE fixture + 3外部デコーダ | 外部アプリ認証、広いPSD E/P/R/Tは別表。全調整・効果・smartobject互換ではない |
| 8 PSD保持と再現 | 未知ブロック等の編集拒否、ガード付き書戻し、merged整合検査、RGBA/Unicode/IDs | 純C#/外部decoder、最大合成差1byteのfixture | 未編集ブロック単位パススルー・部分編集、高bit、色管理、検証済ネイティブ調整、Photoshop/CSP6工程 |
| 9 データモデル | UUID、schema、channel、layer、tile archive。view/brush補助JSON。チャンネル別PSD | 独自保存往復byte一致 | path/model binding指紋、procedural graph/資産保存、複数Texture Set、複雑な正本選択 |
| 10 保存・外部変更 | 完全世代/manifest SHA256/current最後、再読検査、旧世代維持、checkpoint、hash外部改変拒否 | 中断4点/改変/破損/再読込を実行 | OS電源断fsync、低ディスク/lock/全中断点、非同期UI保存、debounceウォッチ、PSD差分再読込と競合解決Undo。現在はSave As/Open |
| 11 Unity責務 | CPU純C#ソース + GPUタイル合成（変わったタイルだけ再合成）、PreviewRenderUtility、リロード/閉じる処理、壊れたシェーダーでの CPU フォールバック | Unity で全アセンブリのコンパイル警告 0、GPU ブラシ誤差 0・GPU 合成誤差 1 以内（llvmpipe） | 実 GPU / D3D11 での整合、Jobs/Burst、AsyncGPUReadbackによるGPU正本、正確な色表示/各API |
| 12 表面・UV | CPU triangle ray、隣接seam、material、連結、表向き/可視性、重複pixel合併、過予算全拒否 | 同じ実装を数学アダプターで実行、細小三角形/近接衣服回帰 | 実画面、BVH/JOB高速化、padding、dilation、3D対称、重複UV制作UI、複雑な非多様体実物 |
| 13 ポーズ/3Dパス | 静的不変snapshot世代の判定。skinnedを除外した不完全モデルは塗れない | 幾何の世代・所有権試験 | ポーズ、BlendShape、BakeMesh、BVH refit、編集可能surface path、再結合、Filled/Ribbon |
| 14 色と精度 | straight RGBA8/隠れRGB保持、データ分離、CPU/GPU ping-pong、encoded-space明記 | 純C#byteテスト、PSDレイヤー一致 | ICC、線形/ガンマ実機表示、16/32bit、HDR、CMYK/Lab、専用法線合成/packing、PSD完全合成 |
| 15 4K/メモリ | 疎/一様タイル、source/rollback/history予算、layer全面GPU非常駐、PSD/staging上限 | core予算atomic拒否/undo、4K sparseケース、4K 4 レイヤー全面でドラッグ中の表示更新 中央値 6.5ms（llvmpipe） | 実RSS/VRAM、disk spill、LRU、低精度preview明示、codec streaming、4K dense大量レイヤー性能 |
| 16 scheduler/履歴 | 確定stroke単位のexact tile Undo/Redo、構造history、取消、予算通知、表示用のタイル変更追跡（ChangeSerial / TryGetChangedTiles） | core回帰・結合試験、変更追跡と差分合成の参照一致 | 構造変更時の部分再合成（今は全タイル。4K で約 2.9 秒）、effect 用の依存 scheduler、halo、global filters、disk history、checkpoint pruning、永続操作ジャーナル |
| 17 実測計画・性能 | 実行環境とテスト結果を分離して記録 | 取得可能なcore実行のみ | 仕様の応答/メモリ/保存workloadの実Unity計測。推測値を合格値にしない |
| 18 受入・故障 | 破損入力/未知PSD/予算超過/中断/改変拒否、元asset保護 | Unity の EditMode で全件、元アセット非変更をウィンドウ経由で確認 | 実device、driver、Play/Reload/複数Window/source scene、ディスク/permission/lock/電源断 |
| 19 gate | 機能するcoreとcodecの検証結果、結合Editorプロトタイプ | Unity 実行で G1 の「元アセット非変更」「保存中断試験」は通過 | G0/G1未合格。実 GPU / Windows、ペンタブ、4K の実メモリ、実外部アプリの確認後に判断 |
| 20-21 参考 | 既存仕様調査の一次資料を継承、追加の検証元を文書化 | 出典リンク | 参照版/実動作保証を混同しない |

## コードがあるがまだUIから使えないもの

- Typed DependencyGraph: 純C#接続/移動/削除検証のみ。制作グラフやGeneratorの完成UIではない
- OrderedBrush shader: 明示GPU試験コマンドのみ。CPU正本の置換前にUnity画素誤差/寿命/Undo/save同期が必要

## 次の実装順

1. ユーザーの実環境（Windows の Unity、実 GPU、ペンタブ）での G0 確認、ウィンドウの保存 UI の実地確認、4K の実メモリ計測
2. 構造変更時の部分再合成、GPU筆跡の非同期tile readback正本同期、BVH/Jobsで先にボトルネックを減らす
3. mask/Fill/selectionと主要native Filter、永続DAG/Anchor、PSD再読込競合UI
4. skinned preview/pose/BlendShape、model fingerprint、編集可能surface pathと再結合
5. Generator/mesh map bake、lilToon版固定アダプター、PSDの型別編集/保持/合成互換拡張

対象Unity 2022.3はユーザー確認済みです。正確なpatch、OS、ペンタブ、lilToon版の確認でテスト対象をさらに絞れます。未対応要望を削除していません。
