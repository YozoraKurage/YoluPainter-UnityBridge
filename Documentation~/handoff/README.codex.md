# dot Texture Painter — G0/G1 prototype

Unity Editor 内の専用ウィンドウで、同じレイヤーへ 2D と 3D から描くための実装プロトタイプです。仕様書 v0.1 の全機能完成版ではありません。Unity Editor 本体でのコンパイル・GPU・実機ペンタブ検証はまだ実行していません。

## 今回の実装

- 疎な RGBA8 タイルを正本にした描画、筆圧・硬さ・間隔・消去、ストローク単位の正確な Undo/Redo/取消
- 2D キャンバス、隔離した静的メッシュの 3D プレビュー、可視性・連結性・UV 継ぎ目を調べる表面描画
- レイヤーの順序・名前・表示・不透明度・Normal/Multiply/Screen、チャンネル別ソース
- 常駐レイヤー全面テクスチャを作らない GPU タイル合成、条件に応じた CPU 代替
- 原画素を保持する独自プロジェクト、チェックサム付き世代保存、保存途中の障害・外部改変の拒否
- 制限を明確にした RGB8 ラスター PSD コーデック、未対応情報を検出した場合の編集禁止
- 後続の Generator/Filter/Anchor 用の型付き依存 DAG 検証

現在の通常描画は **CPU の正本ブラシ**です。GPU ブラシは独立した試験コマンドまでで、実機整合確認前に正本へ組み込んでいません。これを高性能完成版とは呼びません。

## Unity への導入（対象: 2022.3）

1. このフォルダーを展開し、Unity プロジェクトの Package Manager → `+` → Add package from disk を選ぶ
2. `Packages/com.dot.texture-painter/package.json` を指定
3. `Window → dot → Texture Painter (Prototype)` を開く
4. まず 256 または 512 の新規文書で 2D 描画、Undo、Save As、Open を試す
5. まず Demo cube ボタンで6つのUV島を持つ検証用キューブを読み込めます。自分のモデルでは readable な静的 MeshRenderer モデルを Preview model に割り当てる。SkinnedMeshRenderer、非対応/省略された遮蔽物を含むモデルは安全のため描画を止める
6. material slot を選び、同じレイヤー/チャンネルで描く

自動でモデルの Read/Write 設定を変えたり、シーン材質へテクスチャを適用したりしません。プレビューは所有するメッシュ/材質だけを使用します。対象 Unity 2022.3 はユーザー確認済みです。正確な patch、OS、パイプライン、lilToon の版は未確定です。

## 保存

Save As は新しい専用フォルダーを指定します。保存内容は `generations/<id>/` にまとまり、検証後に小さな `current` ポインターを置き換えます。`document.utpaint` が編集可能なネイティブ正本です。対応可能なチャンネルは同じ世代に PSD を生成します。

- 未対応 blend や PSD のメモリ予算超過は、黙ってラスタライズしません。確認後に「ネイティブ保存済み・PSD未更新」と分けます
- 古い世代は消しません。プロトタイプに世代の自動整理はなく、長時間作業ではディスク使用量に注意してください
- 開いた保存世代に外部改変を検出すると通常保存を拒否します。PSD変更の差分表示/三者マージは未実装です
- 元の輸入 PSD を直接上書きしません。編集可能な限定サブセットから読み込んだ元バイト列も保存時に保持します
- 未保存文書の復旧 checkpoint は Unity プロジェクトの Library/DotTexturePainter 配下へ15秒ごと、フォーカス喪失・再読込時にも保存します。突然のプロセス停止では最後の正常 checkpoint までです

## テスト

- `tools/run_all_portable_tests.sh`: 実行可能なportable試験をまとめて実行
- `tools/run_core_tests.sh`: 実際の純 C# ソースを .NET 8 でコンパイルし、描画→Undo→保存→再読込→PSD の結合試験
- `tools/CoreNUnit`: 実際の core/PSD の NUnit 回帰試験（開発専用 NuGet 依存）
- `tools/GeometryHarness`: 同じ幾何ソースを数学アダプターで検証。Unity 動作検証の代替ではありません
- `tools/psd_spike.py`: PSD の独立した構造/外部デコーダ検証
- Unity Test Runner: `Tests/Editor`。詳細は [TESTING.md](docs/TESTING.md)
- Unity 実機: `Window → dot → Run GPU brush parity probe`。同期 readback はこの明示試験だけで使用

現時点の実行結果と未実行ゲートは [VALIDATION.md](docs/VALIDATION.md)、仕様全範囲の状態は [STATUS.md](docs/STATUS.md) を参照してください。

## まだない主要機能

マスク/クリッピング、グループ、調整レイヤー編集、塗りつぶし/選択/変形、Generator/Filter 実行とUI、mesh map ベイク、編集可能な 3D パス、スキン/ポーズ/BlendShape、lilToon 専用出力、ICC/高bit PSD、GPU正本ブラシ、Jobs/Burst、ディスク退避、4Kの実測性能保証。これらは最終スコープから削除していません。

依存ライブラリを含めて配布するライセンス方針はまだ選定していません。現物の制作・検証用プロトタイプとして扱ってください。
