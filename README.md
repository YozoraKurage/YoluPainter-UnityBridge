# YoluPainter

Unity Editor 内の専用ウィンドウで、同じレイヤーへ 2D と 3D から描くテクスチャ制作拡張です。現在は G0/G1 の実装プロトタイプで、仕様書 v0.1（`Documentation~/spec/`）の全機能完成版ではありません。

- Unity 2022.3 以降 / パッケージ名 `net.yozolab.yolupainter` / エディタ専用
- 依存パッケージなし（lilToon への出力は今後の範囲で、今は依存していません）

## 今あるもの

- 疎な RGBA8 タイルを正本にした描画、ストローク単位の正確な Undo/Redo/取消
- ブラシ: 丸/画像の筆先、筆圧・硬さ・間隔・角度・丸さ・ゆらぎ・散布・紙の質感、ストローク内で不透明度を超えない濃さ。内蔵 13 種と Krita 4 既定の筆先 76 種（CC0、`BrushSets~/`）を同梱。GIMP（.gbr / .gih / .vbr）、Photoshop（.abr）、PNG の筆先を取り込める（取り込んだものは Unity プロジェクトの `UserSettings/YoluPainter/Brushes/` に保存。対応しない設定は取り込み時に一覧で知らせる）。CLIP STUDIO の .sut は未対応
- 2D キャンバス、隔離した静的メッシュの 3D プレビュー、可視性・連結性・UV 継ぎ目を調べる表面描画
- レイヤーの順序・名前・表示・不透明度・Normal/Multiply/Screen、ラスターマスク、Fill レイヤー、調整レイヤー（反転・レベル補正・色相/彩度/明度）、クリッピング、チャンネル別ソース（Color / Roughness / Metallic / Height / Normal / Emission）
- 常駐レイヤー全面テクスチャを作らない GPU タイル合成（変わったタイルだけ再合成）、GPU が使えないときの CPU 代替
- 原画素を保持する独自プロジェクト、チェックサム付き世代保存、保存途中の障害・外部改変の拒否
- 制限を明確にした RGB8 ラスター PSD コーデック、未対応情報を検出した場合の編集禁止
- 後続の Generator/Filter/Anchor 用の型付き依存 DAG 検証

通常描画は **CPU の正本ブラシ**です。GPU ブラシは整合確認用の試験コマンドまでで、正本には組み込んでいません。

## 導入（対象: Unity 2022.3）

1. Package Manager → `+` → Add package from disk で、このリポジトリの `package.json` を選ぶ（または Add package from git URL）
2. メニュー `YozoLab → YoluPainter (Prototype)` を開く
3. まず 256 または 512 の新規文書で 2D 描画、Undo、Save As（.ylp）、Open を試す
4. Demo cube ボタンで 6 つの UV 島を持つ検証用キューブを読み込める。自分のモデルでは readable な静的 MeshRenderer のモデルを Preview model に割り当てる。SkinnedMeshRenderer や非対応・省略された遮蔽物を含むモデルは、安全のため描画を止める
5. material slot を選び、同じレイヤー/チャンネルで描く

モデルの Read/Write 設定を自動で変えたり、シーンのマテリアルへテクスチャを適用したりはしません。プレビューは自分で作ったメッシュとマテリアルだけを使います。

## 保存（.ylp）

YoluPainter のファイルは `.ylp` です（CLIP STUDIO の .clip、Photoshop の .psd に当たる 1 ファイル）。中身は zip で、拡張子を .zip に変えれば普通のツールでも中を確認できます。

- `document.utpaint`: 編集可能なネイティブ正本（マスク・Fill・調整・クリッピングを含めてロスなし）
- `composite/<チャンネル>.png`・`thumbnail.png`: 合成済みの画像（正本から作った派生物）
- `view.json`・`brush.json`・`imported-original.psd`（PSD から取り込んだときの原本）
- 先頭の `mimetype`（無圧縮）と、全エントリーの SHA-256 を並べた `manifest.sha256`。読むときに全部確かめ、合わなければ開きません

保存は、作って読み直して確かめた一時ファイル（名前が `~` で終わるので Unity は取り込まない）から最後に 1 回だけ置き換えます。途中で失敗しても元のファイルはそのままです。

- 上書き保存すると、直前の版を隣の `<名前>.ylp-backups~/` に退避します。何世代残すかは設定で選べ、既定はすべて残します（バージョン管理に入れたくなければ `*.ylp-backups~/` を ignore してください）
- 開いた/保存した時点から外でファイルが変わっていると、通常の保存を拒否します（Save As は可）。差分表示・マージは未実装です
- `.ylp` を Assets に置くと、Project ウィンドウにサムネイルと大きさ・チャンネルが出て、ダブルクリックで YoluPainter が開きます。テクスチャは出しません（.ylp は作業ファイルで、配布先に YoluPainter があるとは限らないため、マテリアルから参照させない）。マテリアルには Export Images か lilToon… で書き出した PNG を使ってください
- PSD は「Export PSD」で選んだチャンネルを書き出します。PSD で表せないもの（通常以外の合成モード・マスク・Fill・調整・クリッピング）があると、平らにせず理由を示して書き出しません
- 取り込んだ PSD を直接上書きしません。元のバイト列を `.ylp` の中に保持します
- 未保存の文書の復旧 checkpoint は Unity プロジェクトの `Library/YoluPainter` 配下へ 15 秒ごと（設定で 5〜600 秒）、フォーカス喪失・リロード時にも保存します。突然のプロセス停止では最後の正常 checkpoint までです

## 設定

ウィンドウの Settings ボタン、または Project Settings > YoluPainter で開きます。

- **プロジェクトで共有**（`ProjectSettings/Packages/net.yozolab.yolupainter/Settings.json`、バージョン管理に入る）: 新規ドキュメントの既定の大きさ、共有のブラシ置き場（プロジェクト内のフォルダ。ここへ取り込んだブラシはバージョン管理で全員に渡る）
- **自分だけ**（`UserSettings/YoluPainter/Settings.json`）: 取り込んだブラシの置き場（プロジェクトの外も可。変えるときは今のブラシを複写するか尋ね、元は残す）、同梱ブラシの表示、`.ylp` のバックアップを残す数、復旧 checkpoint の間隔、メモリ予算（Undo 履歴・レイヤーの画素・1 ストローク）

ブラシ置き場は Unity に取り込まれないよう、Assets / Packages の下（名前が `~` で終わるフォルダの中を除く）は指定できません。

## 文書

- `CLAUDE.md` — このリポジトリでの作業ルール
- `Documentation~/STATUS.md` — 仕様全範囲に対する実装状態
- `Documentation~/VALIDATION.md` — 実行した検証と、まだ実行していない検証
- `Documentation~/TESTING.md` — テストの走らせ方
- `Documentation~/ARCHITECTURE.md`、`PSD_COMPATIBILITY.md` — 設計と PSD 互換の範囲
- `Documentation~/spec/` — 仕様書 v0.1 と調査資料
- `Documentation~/handoff/` — Codex からの引き継ぎ時の原文

## まだ無い主要機能

グループ、合成モードの拡充、トーンカーブ等の調整、PSD のマスク/調整/クリッピング/グループの読み書き、塗りつぶし/選択/変形、Generator/Filter の実行と UI、mesh map ベイク、編集可能な 3D パス、スキン/ポーズ/BlendShape、lilToon 専用出力、ICC/高 bit PSD、GPU 正本ブラシ、Jobs/Burst、ディスク退避、4K の実測性能保証。どれも最終スコープから外していません。
