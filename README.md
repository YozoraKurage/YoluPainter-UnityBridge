# YoluPainter

Unity Editor 内の専用ウィンドウで、同じレイヤーへ 2D と 3D から描くテクスチャ制作拡張です。現在は G0/G1 の実装プロトタイプで、仕様書 v0.1（`Documentation~/spec/`）の全機能完成版ではありません。

- Unity 2022.3 以降 / パッケージ名 `net.yozolab.yolupainter` / エディタ専用
- 依存パッケージなし（lilToon への出力は今後の範囲で、今は依存していません）

## 今あるもの

- 疎な RGBA8 タイルを正本にした描画、筆圧・硬さ・間隔・消去、ストローク単位の正確な Undo/Redo/取消
- 2D キャンバス、隔離した静的メッシュの 3D プレビュー、可視性・連結性・UV 継ぎ目を調べる表面描画
- レイヤーの順序・名前・表示・不透明度・Normal/Multiply/Screen、チャンネル別ソース（Color / Roughness / Metallic / Height / Normal / Emission）
- 常駐レイヤー全面テクスチャを作らない GPU タイル合成（変わったタイルだけ再合成）、GPU が使えないときの CPU 代替
- 原画素を保持する独自プロジェクト、チェックサム付き世代保存、保存途中の障害・外部改変の拒否
- 制限を明確にした RGB8 ラスター PSD コーデック、未対応情報を検出した場合の編集禁止
- 後続の Generator/Filter/Anchor 用の型付き依存 DAG 検証

通常描画は **CPU の正本ブラシ**です。GPU ブラシは整合確認用の試験コマンドまでで、正本には組み込んでいません。

## 導入（対象: Unity 2022.3）

1. Package Manager → `+` → Add package from disk で、このリポジトリの `package.json` を選ぶ（または Add package from git URL）
2. メニュー `YozoLab → YoluPainter (Prototype)` を開く
3. まず 256 または 512 の新規文書で 2D 描画、Undo、Save As、Open を試す
4. Demo cube ボタンで 6 つの UV 島を持つ検証用キューブを読み込める。自分のモデルでは readable な静的 MeshRenderer のモデルを Preview model に割り当てる。SkinnedMeshRenderer や非対応・省略された遮蔽物を含むモデルは、安全のため描画を止める
5. material slot を選び、同じレイヤー/チャンネルで描く

モデルの Read/Write 設定を自動で変えたり、シーンのマテリアルへテクスチャを適用したりはしません。プレビューは自分で作ったメッシュとマテリアルだけを使います。

## 保存

Save As は新しい専用フォルダーを指定します。保存内容は `generations/<id>/` にまとまり、検証後に小さな `current` ポインターを置き換えます。`document.utpaint` が編集可能なネイティブ正本です。対応可能なチャンネルは同じ世代に PSD を生成します。

- 未対応の合成モードや PSD のメモリ予算超過は、黙ってラスタライズしません。確認後に「ネイティブ保存済み・PSD 未更新」と分けます
- 古い世代は消しません。世代の自動整理はまだ無いので、長時間の作業ではディスク使用量に注意してください
- 開いた保存世代に外部改変を検出すると通常保存を拒否します。PSD 変更の差分表示・三者マージは未実装です
- 取り込んだ PSD を直接上書きしません。元のバイト列も保存時に保持します
- 未保存の文書の復旧 checkpoint は Unity プロジェクトの `Library/YoluPainter` 配下へ 15 秒ごと、フォーカス喪失・リロード時にも保存します。突然のプロセス停止では最後の正常 checkpoint までです

## 文書

- `CLAUDE.md` — このリポジトリでの作業ルール
- `Documentation~/STATUS.md` — 仕様全範囲に対する実装状態
- `Documentation~/VALIDATION.md` — 実行した検証と、まだ実行していない検証
- `Documentation~/TESTING.md` — テストの走らせ方
- `Documentation~/ARCHITECTURE.md`、`PSD_COMPATIBILITY.md` — 設計と PSD 互換の範囲
- `Documentation~/spec/` — 仕様書 v0.1 と調査資料
- `Documentation~/handoff/` — Codex からの引き継ぎ時の原文

## まだ無い主要機能

マスク/クリッピング、グループ、調整レイヤー編集、塗りつぶし/選択/変形、Generator/Filter の実行と UI、mesh map ベイク、編集可能な 3D パス、スキン/ポーズ/BlendShape、lilToon 専用出力、ICC/高 bit PSD、GPU 正本ブラシ、Jobs/Burst、ディスク退避、4K の実測性能保証。どれも最終スコープから外していません。
