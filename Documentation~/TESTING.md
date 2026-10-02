# テストの走らせ方

テストはすべて Unity の EditMode テスト（`Tests/Editor`、アセンブリ `Yozolab.YoluPainter.Tests`）。.NET 単体のハーネスは使わない。

## devcontainer で

```sh
.devcontainer/unity/test-daemon.sh start --batch-gl   # シェーダー・GPU を確かめるとき（推奨の既定）
.devcontainer/unity/test-daemon.sh restart            # EditorWindow の操作を確かめるとき（GUI モード）
.devcontainer/unity/run-tests.sh                      # 全件
.devcontainer/unity/run-tests.sh --filter 'Yozolab.YoluPainter.Tests.GpuTests'
```

この devcontainer の GUI モードではシェーダーのインクルードが解決できない（VALIDATION.md の「環境の既知の問題」）。そのため 2 つのモードで役割を分ける。

| モード | 動くもの | スキップされるもの |
|---|---|---|
| `--batch-gl`（`-batchmode`、`-nographics` なし、xvfb 上の OpenGL） | core・PSD・保存・幾何・プレビュー・**GPU テスト** | WindowTests（batchmode では EditorWindow の入力が回らない） |
| GUI（既定の `start`） | core・PSD・保存・幾何・プレビュー・**WindowTests** | GpuTests（シェーダーが壊れているので） |
| コールド実行（デーモン無し、`-nographics`） | core・PSD・保存・幾何・CPU 合成 | GpuTests（グラフィックスデバイスが無い）、WindowTests |

シリーズの区切りでは、batch-gl と GUI の両方で全件を回し、合わせて全テストが実行されたことを確かめる。

`--batch-gl` は起動時に、GUI モードで壊れた状態で取り込まれたシェーダーを自動で取り込み直す。

GPU はホストの実 GPU を、WSL2 の `/dev/dxg` と devcontainer に同梱した Mesa 24.2.8 の d3d12 ドライバで使う（OpenGL 4.6）。`gpu-check.sh --unity` で Unity が使っているデバイスを確認できる。GPU テストの CopyTexture 経路と描き込み経路は両方回る。llvmpipe で確かめたいときは `YOLUPAINTER_GPU=0`、システムの Mesa 23.2（OpenGL 4.2。Unity は CopyTexture と compute を無効にする）で確かめたいときは `YOLUPAINTER_MESA=system` を付けてデーモンを起動し直す。

## テストの分類

| フィクスチャ | 中身 | 必要なもの |
|---|---|---|
| CoreTests / PsdTests / ChangeTrackingTests | 純 C# の core、PSD コーデック、変更追跡、タイル単位合成の参照一致 | なし |
| IntegrationTests | 描画→Undo→保存→再読込→PSD、保存の中断・改変検出。一時フォルダの実ファイルシステムに書く | なし |
| GeometryTests | 表面の当たり判定・継ぎ目・可視性・予算、プレビューの所有権 | なし（プレビュー 3 件は PreviewRenderUtility を使う） |
| CompositorTests | 表示の差分更新（CPU 経路を強制） | なし |
| GpuTests（Category `GPU`） | GPU ブラシ・GPU 合成と CPU 正本の画素比較（許容 1 段） | グラフィックスデバイスと正常なシェーダーコンパイラ |
| WindowTests（Category `Window`） | 描画ウィンドウに SendEvent で実際の入力を流す | batchmode でないエディタ |

GPU テストは、組み込みのインクルードすら解決できないエディタではスキップし、パッケージのシェーダーだけが壊れている場合は失敗にする。

## 実機での手動確認（自動化していないもの）

自動テストで代わりにできないもの。結果は VALIDATION.md に、環境（Editor の patch、OS、CPU/GPU/ドライバ、グラフィックス API、色空間、パイプライン、ペンタブのドライバと機種）と一緒に残す。

- 実 GPU（Windows の D3D11 など）で `YozoLab → YoluPainter GPU Brush Parity Probe` を実行し、誤差を記録。リニア色空間のプロジェクトでも
- 実ペンタブ: 筆圧 0〜1 の効き、真の筆圧 0、ペンとマウスの重複、低速・高速、HiDPI、複数モニタ、ボタン、ドライバ更新の前後
- 実際のドメインリロード・Play モード切替・ウィンドウを閉じて開き直す、を描画中に行う
- Save As → Open、復旧 checkpoint、保存中に外部アプリで改変、ディスク満杯・権限エラー・Windows のファイルロック
- 4K の疎・密・多チャンネル・多レイヤーで RSS・VRAM・保存時ピーク・入力から表示までの遅延（中央値・p95・p99）
- Photoshop / CSP での開き直し・保存・再取り込み（外部デコーダだけでは代わりにならない）

コードがある・構文が通る・テストを書いた、だけで「確認済み」にしない。
