# 作業中の分（2026-10-02 に一時停止）

このブランチ（wip/2026-10-02-pause）は、別の PC へ移るために、作業途中の変更を 1 コミットにまとめたもの。
未検証・途中を含むので、そのまま 0.0.0 に入れず、下の残り作業を済ませてから分けてコミットする。
このフォルダ（WIP~）は取り込みが済んだら消す。

## 1. PSD の調整レイヤー（レベル補正・色相/彩度・階調の反転）の読み書き

ファイル: Runtime/Core/Psd/PsdCodec.cs, PsdModel.cs, Runtime/Core/Persistence/PsdBridge.cs,
Tests/Editor/PsdAdjustmentTests.cs（新規）, PsdLayerFeatureTests.cs, PsdGroupTests.cs

済み:
- nvrt / levl（版 2、29 レコード、Lvls v3 拡張は任意。RGB 全体のレコード 0 だけをネイティブに対応、R/G/B は中立が条件）/
  hue2（100 または 136 バイト。マスターの値だけ、色彩の統一オフ・色域の編集なしが条件）の読み込み。
  外れるもの（チャンネル別のレベル補正、色彩の統一、色域の編集、未知の拡張など）は理由付きで保護、壊れたものは拒否。
- 書き出し: PSD の刻み（0〜255 の整数、ガンマ 1/100、色相は整数度、彩度・明度は整数 %）にちょうど乗る設定だけ書く
  （PsdCodec.AdjustmentRefusal）。丸めない。調整が効かないチャンネルでは非表示で書く。
- 合成済み画像の検査に調整レイヤーを含めた。Photoshop の色相/彩度はネイティブの HSL と式が違うので、
  食い違いは保護にせず CompositeDiffers で知らせる判断（保護にすると Photoshop の色相/彩度の層がすべて編集不可になる）。
- クリッピングされたグループの書き出しを拒否（Photoshop の扱いが未確認のため）。読み込みは従来どおり。

残り:
- PsdAdjustmentTests の失敗 5 件はテスト側の誤り:
  1. EachAdjustmentRoundTripsWithOpacityMaskAndClipping: `read.Document.Layers[2].Adjustment` を `Layers[3].Adjustment` に
     （上から [hidden, clipped, shape, plain, bg] の並び）。
  2. SettingsBetweenPsdStepsAreRefusedNotRounded（4 ケース同じ原因）: 最後の行の `AdjustmentSettings.Levels(254 / 255.0, 1)` は
     ネイティブの検査で弾かれる。`AdjustmentSettings.Levels(0, 1 / 255.0)` に替える（"0-253" の拒否の文言に当たるはず）。
- AdjustmentTests.PsdProjectionRefusesAdjustmentLayers は、反転を書けるようになったので往復の試験か「刻みに乗らない設定の
  拒否」の試験に変える。
- Documentation~/PSD_COMPATIBILITY.md に調整レイヤーの行（対応・保護・拒否の条件、CompositeDiffers の判断、hue2 の未使用値の
  NotCarriedIntoExport、調整レイヤーを下地にしたクリッピングは落とすネイティブの規則）と、クリッピングされたグループの
  書き出し拒否を書く。
- psd-tools での確認（調整レイヤー）は未実施。塗りつぶしレイヤー（SoCo）は未着手。
- 全件のテストは未実施。

## 2. 構造変更（スライダー操作など）の高速化

ファイル: Editor/TileGpuCompositor.cs（作り直し）, Shaders/TileComposite.shader, Runtime/Core/SurfaceChangeTracker.cs（新規）,
Tests/Editor/GpuInteractionTests.cs（新規, 10 件通過）, SurfaceChangeTrackerTests.cs（新規, 5 件通過）

済み:
- 約 512px 四方のブロック単位で合成。ブロックごとに、上位の各エントリーが影響を受ける情報の「署名」を持ち、最初に変わった
  エントリーの下の合成結果のコピーが有効ならそこから合成し直す。変わっていないブロックは合成しない。
- 使う層・マスクのブロックは GPU に置いたまま（版が変わったときだけアップロードし直す）。Fill は定数色でアップロードなし。
- 予算 ResidentBudgetBytes（既定 512 MiB）で LRU。溢れたら毎回アップロード（結果は同じ、遅いだけ）。
- どの編集でも、全面の再合成とバイト単位で一致することを確かめた（全モード、グループ、クリッピング、マスク、調整、Undo など）。

残り:
- 速度の計測（目標: 4096²・全面 8 層でスライダー 1 回 100 ms 未満。以前は 1 回 787〜815 ms）。
  WIP~/gpu-interaction-benchmark.cs（unity-do.sh run で実行。doc.SourceBudgetBytes を約 2 GiB に上げる必要がある）。
  上・中・下の層のドラッグ、予算が溢れたとき、最初の全面更新、CPU 代替の時間を測る。
- 全件のテスト（batch-gl と GUI）。
- ResidentBudgetBytes を PainterSettings に出す、フォーカス喪失やアイドルで ReleaseResidentCaches を呼ぶ（ウィンドウ側）。
- SurfaceChangeTracker の代わりに、SparseTileSurface に `public long Revision` と `TileRevision(coord)` を足す
  （TileChanged を出している 3 か所で増やす）のが本来の形。足したら SurfaceChangeTracker を消す。
- グループの中の境目での下のコピー（今は最上位の境目だけ）、CPU 代替の高速化は未着手。

## 3. 変形（移動・回転・拡大縮小）

ファイル: Runtime/Core/PaintDocument.Transform.cs（新規）

済み: Affine2D、PaintDocument.Transform（層の全チャンネルとマスクを一緒に。範囲/選択範囲があればその画素だけを持ち上げて、
跡は透明、行き先で通常で重ねる。補間はプリマルチプライドのバイリニアか最近傍。1 回の Undo、予算超過で全部戻す）、
TransformBounds。コンパイルは通る。
残り: テスト（移動・回転・拡大縮小の画素、選択範囲、マスクが一緒に動く、Undo、予算、Fill/グループの拒否）、
ウィンドウの UI（Move ツールでドラッグ移動、数値で回転・拡大縮小して適用）。
