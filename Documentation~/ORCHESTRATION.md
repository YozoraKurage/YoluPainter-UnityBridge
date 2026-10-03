# エージェントの運用（指揮役・worktree・止める・再開する）

YoluPainter は、指揮役（メインの Claude）と、作業者のエージェント（Claude のサブエージェント、後で Codex）で並行して開発する。
この文書は、コンテナの再起動・作り直し・異常終了をまたいで、エージェントが仕事を正しく続けるための決まりと手順。
テストの台とキューは TESTING.md、掲示板の使い方は `~/agent-board/README.md`。

## 何がどこにあって、何が消えるか

| もの | 置き場 | コンテナの再起動 | 作り直し |
|---|---|---|---|
| 統合の木（0.0.0）と各エージェントの worktree（コミット・未コミットの変更・進捗メモ） | `/workspace`（ホストのディスク）、`/workspace/.worktrees/<名前>` | 残る | 残る |
| GitHub に push したもの（0.0.0、agent-board） | GitHub | 残る | 残る |
| エージェントの頭の中（どこまでやったか・次に何をするか・迷っていること） | 会話の中だけ | **消える** | **消える** |
| 会話の記録（指揮役とサブエージェントの transcript）とメモリ | `~/.claude`（名前付きボリューム） | 残る | 残る |
| テストの台 0（`~/unity-testproject`） | 名前付きボリューム | 残る（Unity は起動し直し） | 残る |
| テストの台 1 以上（`~/unity-runners`）・掲示板のデータ（`~/.local/share/agent-board`）・`~/agent-board` | コンテナの中 | 残る（起動し直し） | 消える（起動時に作り直す・取り直す） |

**消えるのはエージェントの頭の中だけ**なので、それを worktree に書き残すことがいちばん大事。

## エージェントの決まり（書き残し）

- 指揮役は、エージェントを立てるときの依頼文を、その worktree の **`.agent/task.md`** に保存する（再開した人が読む）。
- 自分の worktree に **`.agent/progress.md`** を置き、区切りごと（目安 30 分ごと、テストを回した後、方針を決めた後）に書き直す。
  `.agent/` は git でもテストの台への同期でも無視される。形:

  ```markdown
  # <タスクの名前>（担当 <名前>、ブランチ agent/<名前>）
  状態: 作業中 / 待ち / 一時停止 / 終わり    最終更新: 2026-10-03 15:00
  ## 済んだこと
  ## 次にすること（再開した人がそのまま始められる粒度で）
  ## 最後のテスト（コマンドと結果）
  ## 決めたこと・採らなかった案（コミットメッセージの材料）
  ## 迷っていること・ユーザーに聞きたいこと
  ```
- 途中でも、ビルドの通る区切りで **`wip:` のコミット**を自分のブランチに残す（統合のときに指揮役がまとめる）。
- **一時停止の指示**（指揮役からの「一時停止」）を受けたら: 今の手を止め、ビルドが通らなければ通る所まで戻すか `wip:` として理由を書いて
  コミットし、`.agent/progress.md` を「一時停止」で書き直し、指揮役に短く報告して終わる。テストの依頼を待っているなら待たずに終わる。

## Codex の担当

Codex（OpenAI の Codex CLI）も Claude の担当と同じ決まりで動く。Codex はリポジトリの `AGENTS.md` を読み、そこから CLAUDE.md と
この文書へ進む。

- 準備（1 回）: `npm install -g @openai/codex`、ユーザーが `codex login --device-auth`（ブラウザで ChatGPT のアカウント）。掲示板の
  トークンは `agent-board token issue codex --kind codex` で `~/.local/share/agent-board/codex.token` に置き、`codex mcp add board --url
  http://127.0.0.1:8787/mcp --bearer-token-env-var AGENT_BOARD_TOKEN_CODEX` で登録する（名乗れる名前は codex と codex-*）。
- 立てる: 指揮役が Claude の担当と同じく worktree を作り、依頼文を `.agent/task.md` に置いて
  `.devcontainer/orchestration/codex-run.sh <名前>` で裏で走らせる（モデルと推論の強さは `CODEX_MODEL`・`CODEX_EFFORT` か
  `--model`・`--effort`。ユーザーの指定は gpt-6.1-sol・xhigh で、これが既定）。様子は `codex-run.sh <名前> --status`、止めるのは `--stop`。
- 許可: Codex の自動の審査（`--approve-for-me`、Claude の auto mode に当たる）。この devcontainer では Codex のサンドボックス（bwrap）が
  名前空間を作れない（2026-10-03 に確かめた）ので、どのコマンドもまずサンドボックスで失敗し、「外で実行する」申請を審査役のモデルが
  通したものだけが外で動く。審査も無しの `--bypass` はユーザーの許しがあるときだけ。どちらでも Codex のプロセスには VS Code の
  資格情報を渡さず、`origin` への push の宛先を無効にする（うっかりの push は通らない。わざと回避されれば防げない）。
- 報告は `.agent/codex-last.md`（最後の返答）と `.agent/progress.md`。終わったら指揮役が読んで統合する。
- 一時停止: `codex-run.sh <名前> --stop` で止める。止める前に progress.md が新しいかを見る（Codex は止められると書き残せないので、
  区切りごとの書き残しと wip のコミットが頼り）。再開は同じ worktree で、依頼文に「progress.md の『次にすること』から続ける」と足して立て直す。

## 止めるとき（コンテナの再起動・作り直しの前）

1. 指揮役が、動いているエージェント全員に「一時停止」を送り、全員の報告を待つ（Codex の担当は `codex-run.sh <名前> --stop`）。
2. `.devcontainer/orchestration/status.sh` で、各 worktree の進捗メモが新しく、ビルドが通るコミットがあることを確かめる。
3. 統合の木（`/workspace`）に未コミットの変更があれば、コミットするか、理由を残す。push できるものは push する（作業ブランチ 0.0.0 と agent-board）。
4. `.devcontainer/orchestration/stop-all.sh` でテストの台と掲示板を止める（書き残しが古い worktree があれば止まって一覧を出す。`--force` で構わず止める）。

## 再開するとき（再起動・作り直しの後）

1. コンテナが起動すると `start-all.sh`（postStartCommand）が掲示板を起動し（無ければ GitHub から取る）、テストの台を裏で 1 台ずつ起動する
  （作り直した後は台 1 以上を作り直すので数分。`~/unity-runners/start-all.log`）。人の画面のトークンは作り直した後は発行し直す。
2. 指揮役（`claude --continue` か新しい会話）は、メモリ（指揮役の決まり）とこの文書を読み、`status.sh` で全体を見る。
3. 作業中だった worktree ごとに、新しいエージェントを立てる。依頼は「`/workspace/.worktrees/<名前>` の `.agent/task.md`（元の依頼文）と
   `.agent/progress.md`、ブランチのコミット（`git log 0.0.0..`）を読んで、『次にすること』から続ける」。
4. テストの台が揃うまでは、`run-tests.sh` は動いている台に振り分けるか、空くのを待つ。

## 異常終了したとき（電源断・コンテナの強制終了・エージェントの途中終了）

- **エージェントが途中で終わった**（上限・誤り）: worktree はそのまま残る。`status.sh` で進捗メモとコミットを見て、上の「再開するとき」の 3 と同じく
  新しいエージェントに続けさせる。進捗メモが無い・古いときは、`git -C <worktree> status` と `git diff` から状態を読み取って依頼に書く。
- **テストの台の Unity が落ちた・固まった**: `runners.sh status` で down なら `runners.sh start <番号>`。固まった（鼓動が止まったまま）なら
  `runners.sh restart <番号>`。依頼していた側は終了コード 5 か「デーモンが死んでいた」で終わるので、回し直す。台 0 は `test-daemon.sh restart --batch-gl`。
- **掲示板が落ちた**: `agent-boardctl start`。実行中だったテストの依頼は「中断」で閉じて、待っていた人に知らせる（自動ではやり直さない）。
- **統合の途中で落ちた**: 統合はコミットの直前まで `scratchpad` の写しで行い、0.0.0 には最後の 1 回のコミットでしか書かない（commit-snapshot）。
  落ちても 0.0.0 は前のコミットのまま。`git status` で統合の木に半端な変更が無いかを見る。
- **異常終了で worktree の中のファイルが壊れた**: ブランチの最後のコミットに戻せる（`wip:` をこまめに残す理由）。戻すのはそのエージェントか
  指揮役が中身を見てから（黙って消さない）。

## 指揮役の決まり（抜粋。詳しくは指揮役のメモリ）

- 新しい担当は、今の HEAD から作った専用の worktree（`git worktree add /workspace/.worktrees/<名前> -b agent/<名前> HEAD`）で動かす。
  Agent ツールの isolation: worktree は main の最初のコミットから作られたので使わない。
- 0.0.0 に入れるのは指揮役だけ。入れる前にその木で `run-tests.sh --both` を回す。共有の文書（STATUS・VALIDATION・README）は、HEAD に担当の
  行だけを足した版で入れる（ファイルを丸ごと取らない。2026-10-03 に別の担当の書きかけの節が混ざった）。
- エージェントの依頼文には、この文書の「エージェントの決まり（書き残し）」を入れる。
