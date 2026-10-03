# テストの走らせ方

テストはすべて Unity の EditMode テスト（`Tests/Editor`、アセンブリ `Yozolab.YoluPainter.Tests`）。.NET 単体のハーネスは使わない。

## devcontainer で

```sh
.devcontainer/unity/test-daemon.sh start --batch-gl   # シェーダー・GPU を確かめるとき（推奨の既定）
.devcontainer/unity/test-daemon.sh restart            # EditorWindow の操作を確かめるとき（GUI モード）
.devcontainer/unity/run-tests.sh                      # 全件
.devcontainer/unity/run-tests.sh --filter 'Yozolab.YoluPainter.Tests.GpuTests'
.devcontainer/unity/run-tests.sh --filter 'Yozolab.YoluPainter.Tests.(WindowTests|ShortcutGuardTests)[.]'   # 複数のクラス
```

`--filter` はテストの完全名に対する正規表現（Unity の Test Framework の groupNames）。`;` で区切っても複数にはならず 0 件になる。
依頼は JSON で送るので、`\.` のようなバックスラッシュは使えない（JSON の読み取りで落ちる）。点そのものは `[.]` と書く。

この devcontainer の GUI モードではシェーダーのインクルードが解決できない（VALIDATION.md の「環境の既知の問題」）。そのため 2 つのモードで役割を分ける。

| モード | 動くもの | スキップされるもの |
|---|---|---|
| `--batch-gl`（`-batchmode`、`-nographics` なし、xvfb 上の OpenGL） | core・PSD・保存・幾何・プレビュー・**GPU テスト** | WindowTests（batchmode では EditorWindow の入力が回らない） |
| GUI（既定の `start`） | core・PSD・保存・幾何・プレビュー・**WindowTests** | GpuTests（シェーダーが壊れているので） |
| コールド実行（デーモン無し、`-nographics`） | core・PSD・保存・幾何・CPU 合成 | GpuTests（グラフィックスデバイスが無い）、WindowTests |

シリーズの区切りでは、batch-gl と GUI の両方で全件を回し、合わせて全テストが実行されたことを確かめる。

### テストの台（runners）とキュー

常駐 Unity を何台か並べ、依頼を空いている台へ振り分ける（2026-10-03 から。設計の考え方は、マルチエージェント協調基盤の設計草案の段階 1）。

| 台 | プロジェクト | パッケージ | モード |
|---|---|---|---|
| 0 | `~/unity-testproject`（名前付きボリューム） | `/workspace` を直接読む | `switch-daemon.sh` で変わる |
| 1 | `~/unity-runners/1/project` | 台の中の写し `~/unity-runners/1/pkg` | batch-gl（`runners.conf`） |
| 2 | `~/unity-runners/2/project` | 台の中の写し `~/unity-runners/2/pkg` | GUI（`runners.conf`） |
| 3 | `~/unity-runners/3/project` | 台の中の写し `~/unity-runners/3/pkg` | batch-gl（`runners.conf`。計測やエージェントの worktree 用に足した） |
| 4 | `~/unity-runners/4/project` | 台の中の写し `~/unity-runners/4/pkg` | GUI（`runners.conf`。GUI の依頼が詰まったので足した） |
| 5 | `~/unity-runners/5/project` | 台の中の写し `~/unity-runners/5/pkg` | GUI（`runners.conf`。担当 4 人が同時に GUI の全件を待ったので足した） |

```sh
.devcontainer/unity/runners.sh setup        # 台 1・2 を作る（台 0 から設定とパッケージを写し、コールドで取り込む。1 台 40 秒ほど）
.devcontainer/unity/runners.sh start        # runners.conf のモードで常駐させる
.devcontainer/unity/runners.sh status       # 台ごとのモードとパッケージの出どころ
.devcontainer/unity/run-tests.sh --filter '…'                  # 空いている batch-gl の台（1 → 0 の順）
.devcontainer/unity/run-tests.sh --mode gui --filter '…'       # GUI の台
.devcontainer/unity/run-tests.sh --both --sha HEAD             # コミットの木で、batch-gl と GUI の台で同時に全件
.devcontainer/unity/run-tests.sh --source /path/to/worktree    # 別の worktree のパッケージで回す
.devcontainer/unity/unity-do.sh --runner 1 run -e '…'          # 台を選んでスニペット
.devcontainer/unity/unity-do.sh --runner 3 --source ~/wt run -e '…'   # 台 1 以上で、このフォルダに同期してからスニペット
```

エージェントの worktree: 共有の作業ツリーで書きかけを保存し合わないよう、エージェントは自分の worktree（`/workspace/.worktrees/<名前>`、ブランチ
`agent/<名前>`。`.git/info/exclude` で無視し、テストの台への同期でも写さない）で作業してコミットし、テストは `--source <worktree>` で台 1 以上へ
回す。統合はメインがする。Agent ツールの `isolation: worktree` は main の最初のコミットから作られたので使わない（2026-10-03）。

- 台 1 以上は、依頼のたびにソース（既定は `/workspace` の作業ツリー、`--sha` ならそのコミットを `git archive` で、`--source` ならそのフォルダ）を
  台の中の写しへ同期してから回す（`sync-package.py`。中身の違うファイルだけを写すので、Unity は変わった所だけを取り込み直す。
  `temp~`・`.git`・`.devcontainer`・`.github` は写さない）。テストの最中にほかの作業者が保存しても、その回には入らない。
- `--sha` と `--source` は台 1 以上だけ（台 0 は `/workspace` を直接読むので選べない）。どの台が何を回したかは出力の 1 行目に出る。
- 空いている台は、台ごとの依頼のロック（`TestDaemon/client.lock`）を取り合って決める。全部が埋まっていれば空くのを待つ。
- **統合用の台**（`runners.conf` の 3 列目 `integration`。今は台 1（batch-gl）と台 5（GUI））: 指揮役の統合は `--priority` で、統合用の台を
  先に使い、重い試験も回す。担当の全件（絞り込み無し。組に分けた子も）は統合用の台を使わない（短い絞り込みは使ってよい）。担当の全件が
  統合を待たせないため（2026-10-03、担当 7 人のとき統合の全件が 24 分待った）。
- **重い試験**（`Tests/Editor/Support/SlowTests.txt`、1 件 4 秒以上のもの 20 件・batch-gl の試験の時間の約 4 割）: 絞り込みの無い全件では既定で
  飛ばし、`--full` か `--priority` で回す。飛ばすのは「テストのメソッドの完全名」だけに当たる正規表現（組の名前に当たると中の全部が回る）。
  一覧の行が今ある試験を指していることは GuiOnlyFixturesTests.EverySlowTestEntryNamesAnExistingTest が確かめる。
- **全件の後の再起動**: 全件（組に分けた子も）を回した台は、使い終わりに裏で再起動する。WSL の GPU のドライバの層（Mesa の d3d12 ⇔ Windows）が、
  Unity が捨てたテクスチャ・描き先の分を Windows の「共有 GPU メモリ」に溜め込み、プロセスが終わるまで返さないため（2026-10-03: Unity 自身は
  GPU のメモリ 0.03 GB と答える GUI の台を 1 つ止めると、共有 GPU メモリが 37.4 → 27.3 GB に減った。溜まりすぎてコンテナが落ちた）。
  RAM で太った台（本体と取り込みの手伝いの合計が 4,500 MB 超）も同じく再起動する。
- 全件を組に分けて回す: `--shards N`（`auto` = そのモードの、今空いている台の数。3 まで、空きが無ければ 1 組で待つ）。`--both` の全件は GUI の側だけが `--shards auto`
  （GUI でしか回らないテストは主スレッドの窓の描画が律速で、3 台に分けて 1 組 114〜177 s。分けないと 370〜420 s）。batch-gl の側は分けない
  （1 つの台の中でも合成や焼きが全部のコアで並列に走るので、2 台に分けても 1 組 290〜490 s で、分けない 340 s より速くならなかった）。
  分け方は `shard-filters.py`（単位はテストのソースのクラス。GUI の側の WindowTests はメソッド名の頭の文字ごと。重さはテストごとの
  時間の履歴の中央値で、重い順にいちばん軽い組へ）。全部のテストのときは、最後の組が「既知のクラスのどれでもない完全名」も拾う
  （ソースから取りこぼしても回らないことは無い）。組ごとの出力の後に合計の 1 行（件数と、いちばん長い組の時間）を出す。台が埋まって
  いれば組は空くのを待つので、混んでいるときは分けない場合とほぼ同じ時間になる。
- `--both` で絞り込みが無い（全件の）とき、GUI の台では GUI でしか回らないテストのクラス（`Tests/Editor/Support/GuiOnlyFixtures.txt`）だけを回す
  （`--gui-only`）。ほかのテストは batch-gl の台で回っているので、2 回回さない。新しく「batch-gl なら飛ばす」テストのクラスを作ったら、
  この一覧に 1 行足す（足し忘れは GuiOnlyFixturesTests が落とす）。GUI の台で全部を回したいときは `--mode gui` だけを付ける。
- GUI の台では、窓のテストが CPU で表示を合成するので、続けて実行されるテストの塊で常駐の鼓動が 3 分を超えて止まることがある。そのため GUI の台では、
  鼓動が止まったと判断するまで 15 分、全体の待ちの上限を 40 分にしている（batch-gl は 3 分と 15 分）。GUI の全件は 14 分ほどかかる。
- `switch-daemon.sh gui -- …` は、GUI の台が動いていれば台 0 を切り替えずにその台で回す（切り替えの数分を待たない）。
- テストごとの時間: 常駐 Unity が 1 件ずつ `TestDaemon/durations.tsv` に書き、run-tests.sh が実行の後に
  `~/.cache/yolupainter-tests/durations/<日時>-runner<番号>-<モード>.tsv` へ写す（新しい 200 回分）。`test-durations.sh`
  （`--mode gui`・`--runs 5` で中央値・`--top 30`・`--list`）がクラスごとの合計と遅い順を出す。常駐側を変えたので、台ごとに
  一度 `runners.sh restart <番号>`（台 0 は `test-daemon.sh restart`）するまで、その台では記録されない。
- 台 1 以上のプロジェクトは名前付きボリュームではないので、コンテナを作り直すと消える（`runners.sh setup` で作り直す）。台 0 の Library は
  写さない（開いている Unity の Library を写すと、データベースが壊れた写しになり得る）。台 0 の Assets（`ZZ_UserAssets` を含む）も写さない。
- 資源の目安: 6 台（台 0〜5）を常駐させても、この PC（メモリ 58 GB・32 スレッド）では足りている。GPU は WSL2 の d3d12 を共有する。

### エージェントの掲示板（agent-board）

複数のエージェントの仕事・状態・メッセージ・ユーザーへの質問・テストの依頼を置く掲示板は、別のリポジトリ
（https://github.com/YozoraKurage/agent-board 、コンテナの中では `~/agent-board`）にある。devcontainer の起動ごとに
`.devcontainer/agent-board-start.sh`（postStartCommand）が立ち上げ、無ければ GitHub から取る。

- 画面: `http://localhost:8787/`（VS Code が 8787 番を転送する）。人のトークンは `~/agent-board/bin/agent-board token issue user --kind human`
  で発行する（表示は 1 回だけ。コンテナを作り直すと掲示板のデータと一緒に消えるので、発行し直す）。
- Claude Code には MCP（`board`、ユーザーの設定）として登録してある。トークンは `~/.local/share/agent-board/claude.token` を `~/.zshrc` の
  `AGENT_BOARD_TOKEN` が読む（設定ファイルには平文で残さない）。登録した後に起動した Claude Code から使える。
- エージェント向けの決まりの案は `~/agent-board/docs/agent-rules.md`。使い方は `~/agent-board/README.md`。

### 複数の作業者（エージェント）が同時にデーモンを使うとき

デーモンへの依頼の受け口は 1 組しか無いので、`run-tests.sh` と `unity-do.sh` は `TestDaemon/client.lock` を flock で取り、依頼を 1 本ずつ通す（後から来た依頼は待つ）。モードの切り替えは `test-daemon.sh` を直接使わず、`switch-daemon.sh` を使う:

```
.devcontainer/unity/switch-daemon.sh gui -- --filter 'Yozolab.YoluPainter.Tests.WindowTests'   # GUI で回して batch-gl に戻す
.devcontainer/unity/switch-daemon.sh batch-gl                                                  # batch-gl へ
```

切り替えの間は `TestDaemon/switching` を置き、ほかの依頼はそれが消えるまで待つ（デーモンがいない一瞬にコールド実行へ落ちて、起動中のデーモンとプロジェクトを取り合わない）。実行中の依頼があれば、それが終わってから切り替える。常駐させる Unity にはロックのファイルを引き継がせない（引き継ぐと、その Unity が生きている間ロックが外れず、誰の依頼も通らなくなる。実際に起きた）。

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
