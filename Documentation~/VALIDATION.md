# 検証記録

最終更新: 2026-10-02（Claude Code。初めて実際の Unity Editor で検証した）

「コードがある」「構文が通る」と「実行して確かめた」を混同しない。各行は実際に実行した結果だけを書く。失敗した検証も消さず、原因と再試験を残す。

## 実行環境（2026-10-02）

| 項目 | 値 |
|---|---|
| Unity | 2022.3.22f1（game-ci の `unityci/editor:ubuntu-2022.3.22f1-base-3` イメージ、Personal ライセンス） |
| OS | Ubuntu 22.04（Docker）on WSL2（Linux 6.6.87.2-microsoft-standard-WSL2）、ホストは Windows |
| CPU / RAM | AMD Ryzen 7 5700X（16 スレッド）/ 24 GB |
| グラフィックス | OpenGLCore 4.5 / **llvmpipe（Mesa 23.2.1 のソフトウェアレンダラ）** on xvfb。実 GPU ではない |
| 色空間 / パイプライン | Gamma / Built-in |
| テスト用プロジェクト | `/home/node/unity-testproject`（devcontainer のボリューム）。このリポジトリを `file:/workspace` で参照し、`testables` に `net.yozolab.yolupainter` |
| lilToon | 2.3.4（`jp.lilxyzw.liltoon`、VPM で導入。テストプロジェクトのみ。パッケージは依存していない） |

## Unity で実行したもの

| 段階 | 結果 | 備考 |
|---|---|---|
| パッケージの取り込みと C# コンパイル（Core / Editor / Tests の 3 アセンブリ） | **成功、警告 0** | 引き継ぎ時のコードは無修正でコンパイルが通った |
| シェーダーのコンパイル（PreviewSurface / TileComposite / OrderedBrush） | **成功**（batch-gl） | GUI モードの devcontainer では環境の問題で失敗する（下の「環境の既知の問題」） |
| EditMode テスト全 113 件 | **全件実行・失敗 0** | batch-gl で 101 成功 / 12 スキップ（ウィンドウ）、GUI で 109 成功 / 4 スキップ（GPU）。どの 1 件もどちらかのモードで実行されて通っている |
| 　CoreTests | 31/31 | 引き継ぎ時のテスト |
| 　PsdTests | 28/28 | 引き継ぎ時のテスト（500 件の変異入力を含む） |
| 　IntegrationTests | 8/8 | 引き継ぎ時の .NET ハーネス（CoreHarness）から移植。保存の 4 つの中断点、current 置換後の応答喪失、古いトークンの拒否、改変検出、PSD 往復。実ファイルシステムに書く |
| 　GeometryTests | 22/22 | 純幾何 19 件 + Unity 専用のプレビュー 3 件（引き継ぎ時は未実行だった） |
| 　ChangeTrackingTests | 7/7 | 新規。変更追跡、CopyTile、タイル単位 CPU 合成と 1 画素ずつの参照実装のバイト一致 |
| 　CompositorTests | 1/1 | 新規。CPU 経路の差分更新が各段階で参照と一致 |
| 　GpuTests | 4/4（batch-gl） | 新規。下の GPU 整合 |
| 　WindowTests | 12/12（GUI） | 新規。下のウィンドウ操作 |
| GPU ブラシ整合（OrderedBrush のダブ vs CPU ブラシ、32px） | **最大誤差 0、平均 0** | llvmpipe。メニューの整合プローブと同じ計測をテスト化 |
| GPU タイル合成 vs CPU 参照合成 | **最大誤差 1 以内**（RGBA8 の丸め 1 段） | Normal / Multiply / Screen、不透明度、非表示、無効チャンネル、透明画素の RGB、部分タイル、差分更新の各段階 |
| ウィンドウ操作（SendEvent で実際の IMGUI 経路に入力） | 12/12 | 点の位置（左下原点）、Ctrl+Z / Ctrl+Shift+Z のバイト一致、Escape・フォーカス喪失・リロード前・Play 移行での取消、ウィンドウ外で離しても確定、筆圧 0 で塗らない、デモキューブの 3D 描画、2 ウィンドウの独立、閉じたあと RenderTexture / Mesh / Material が元の数に戻る |
| 元アセット非変更（ゲート 9） | 成功 | シーンのモデルを読み込み 3D で塗った後も、マテリアル・テクスチャ・色・頂点・UV・Transform・コンポーネント数・dirty カウントが不変 |

### 性能の実測（llvmpipe、Unity 上。実 GPU の値ではない）

4 レイヤーが全面に埋まったドキュメント（タイル 128px）で、ストローク中の 2D 表示更新にかかった時間:

| | 変更前（毎回全タイルを再合成） | 変更後のドラッグ 1 回（中央値 / p95） |
|---|---|---|
| 1024px・GPU 合成 | 178 ms | 5.6 / 11.1 ms |
| 4096px・GPU 合成 | 2881 ms | 6.5 / 11.9 ms |
| 4096px・CPU フォールバック | 5720 ms | 35.6 / 46.7 ms |
| ウィンドウでのドラッグ 1 イベント（1024px、GUI の CPU フォールバック） | 2058 ms | 25 ms |

構造の変更（不透明度・合成モード等）は今も全タイルを再合成する（4096px で約 2.9 秒）。プロセスの RSS・VRAM・保存時のピークメモリ・入力から表示までの遅延は未計測。

## Unity 検証で見つかって直したもの

| 問題 | 原因 | 対処（コミット） |
|---|---|---|
| GPU 合成が 2 枚目以降のレイヤーで全画素ずれる（最大誤差 179） | ping/pong 作業タイルがバイリニア。前段の結果を読むとき隣の画素と補間された（llvmpipe で顕在化） | 作業タイルをポイントサンプリングに（27cd620） |
| コンパイルに失敗したシェーダーでも GPU 経路を選び、表示がマゼンタになる | `Shader.isSupported` はパスが除外されたシェーダーにも true を返す | `ShaderUtil.ShaderHasError` も見て CPU 合成に落とす（6e1f9eb） |
| ストローク中のマウスイベント 1 回に約 2 秒（1024px） | 変更のたびに全タイル（CPU は全画面）を、1 画素ずつ辞書を引いて再合成していた | core に変更追跡とタイル読み出し（c3b8faf）、表示を差分更新に（53710e6） |

## 環境の既知の問題（devcontainer）

- **GUI モードの Unity ではシェーダーのインクルードが解決できない。** 組み込みの `HLSLSupport.cginc` すら開けず、CGPROGRAM のシェーダーは組み込みのものも含めて全部エラー（描画はマゼンタ）。`-batchmode`（`-nographics` なし、xvfb 上）では同じプロジェクト・同じ Library で正常。起動ディレクトリ、プロジェクトパス、ボリュームか否か、ドメインリロードは無関係と切り分けた。根本原因は Unity 内部で未特定。
- GUI モードで取り込まれたシェーダーはエラーごと Library に残るので、`test-daemon.sh start --batch-gl` は起動時にエラーを抱えたシェーダーを取り込み直す。
- このため、シェーダー・GPU の検証は batch-gl、EditorWindow の操作は GUI モードで行う（TESTING.md）。

## 実行していないもの・確立していないもの

- **実 GPU と D3D11 / Vulkan / Metal**（ここは llvmpipe の OpenGL のみ）。ユーザーの実環境（Windows の Unity）での確認が必要
- 実ペンタブ（筆圧の値の質、ペンとマウスの重複、ボタン）、HiDPI、複数モニタ、入力から表示までの遅延
- 実際のドメインリロードと Play モード切替の最中の挙動（テストは通知メソッドを直接呼んで代用）
- ウィンドウの Save / Save As / Open / Import PSD / Export PNG（ファイル選択ダイアログがあるため未自動化。保存処理そのものは IntegrationTests で実ファイルシステムに対して検証済み）
- Photoshop / CLIP STUDIO PAINT での往復、16/32bit・ICC、ネイティブ調整・スマートオブジェクト・テキスト
- 4K の実メモリ（RSS・VRAM）、ディスク退避、低メモリ時の挙動
- 電源断・fsync、Windows のファイルロック、ディスク満杯、権限エラー
- lilToon への出力・見た目の一致（プレビューは中立シェーダー。lilToon は入れただけ）
- **G0 / G1 の合格判定。** G1 の「元アセット非変更」「保存中断試験」は通過、「4K の基本ワークロード」は表示更新の時間だけ。G0 の筆圧・lilToon 版・実 OS は実機待ち

## 引き継ぎ時の検証（Codex、2026-10-02、Unity なしの隔離環境）

Unity の無い環境で、.NET 8 SDK を使って同じソースを検証していた。結果の要約だけ残す（生ログ・ハーネスは ca64f42 で取り込み、ec1b750 で削除。git の履歴から参照できる）。

- 純 C# の core / PSD NUnit 59/59（core 31 + PSD 28）、結合シナリオ 8/8、.NET Standard 2.1 コンパイル 警告 0
- 幾何を Unity の数学アダプターで 19/19（Unity 自体の実行ではない）
- C#8 構文検査 20 ファイル エラー 0（Unity API の型解決・シェーダーは含まない）
- PSD: 独立に生成した raw / RLE fixture を実 C# で読み書き、独立バイトパーサ・Pillow 11.3.0・psd-tools 1.10.9・ImageMagick で確認。レイヤー RGBA はバイト一致、独立合成との差は最大 1 バイト。未対応 PSD の原本保持と編集拒否も確認
