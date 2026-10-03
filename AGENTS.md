# AGENTS.md — YoluPainter（Codex などのエージェント向けの入口）

このリポジトリの決まりは **`CLAUDE.md` に全部書いてある**。Claude 向けの名前だが、Codex を含むどのエージェントにもそのまま当てはまる。
最初に `CLAUDE.md` を読み、その「読む順序」「不変条件」「ユーザー固有データを git 履歴に絶対に残さない」「コミットメッセージは設計判断の
一次資料」「テストの走らせ方」に従うこと。

作業者（指揮役から仕事を受けた担当）として動くときは、続けて `Documentation~/ORCHESTRATION.md` を読む。要点:

- 作業場所は指揮役が作った専用の worktree（`/workspace/.worktrees/<名前>`、ブランチ `agent/<名前>`）だけ。`/workspace` の本体と
  ほかの worktree は書き換えない。push しない。`0.0.0` や `main` に直接コミットしない（統合は指揮役がする）。
- 依頼文はその worktree の `.agent/task.md`。進捗メモ `.agent/progress.md` を区切りごとに書き直し、ビルドの通る区切りで `wip:` の
  コミットを自分のブランチに残す。
- STATUS.md と VALIDATION.md は書き換えない（報告に案を書く）。
- 返答・コミットメッセージ・進捗メモ・文書は日本語。Unity の用語はカタカナ（ゲームオブジェクト、マテリアル、シェーダー など）。
- テストは `.devcontainer/unity/run-tests.sh --source <自分の worktree>`（詳しくは `Documentation~/TESTING.md`）。テストの台の起動・
  停止・切り替えはしない。
- 掲示板（agent-board、MCP の名前 `board`）が使えるなら、名乗り（`board_checkin`）と、終わったときの報告に使う。
