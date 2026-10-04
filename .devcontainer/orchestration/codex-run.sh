#!/bin/bash
# Codex の担当を、指揮役が作った worktree で裏で走らせる（開発の文書 /workspace/temp~/dev/ORCHESTRATION.md の「Codex の担当」）。
#
#   codex-run.sh <名前> [--model M] [--effort E] [--bypass]       # /workspace/.worktrees/<名前>/.agent/task.md の依頼で始める
#   codex-run.sh <名前> --status                                    # 動いているか・最後の返答
#   codex-run.sh <名前> --stop                                      # 止める（途中の変更は worktree に残る）
#   codex-run.sh <名前> --resume "<伝えること>"                     # 前のセッション（codex.log の session id）を文脈ごと続ける
#
# - 依頼文は worktree の .agent/task.md（Claude の担当と同じ形）。Codex にはそれを読んで従うようにだけ言う。
# - 出力は .agent/codex.log、最後の返答（報告）は .agent/codex-last.md、PID は .agent/codex.pid。
# - 既定のモデルと推論の強さは gpt-6.1-sol・xhigh（ユーザーの指定。CODEX_MODEL・CODEX_EFFORT か --model・--effort で変える）。
# - 許可は Codex の自動の審査（--approve-for-me。Claude の auto mode に当たる）。この devcontainer では Codex のサンドボックス（bwrap）が
#   名前空間を作れないので、どのコマンドもまずサンドボックスで失敗し、「外で実行する」申請を審査役のモデルが通したものだけが外で
#   動く（2026-10-03 に確かめた）。--bypass は審査も無しで全部を実行する（ユーザーの許しがあるときだけ）。
# - どちらでも、Codex のプロセスには push の資格情報を渡さない（下の scrub）。
set -euo pipefail
name="${1:?担当の名前（/workspace/.worktrees/<名前>）が要る}"; shift
WT="${CODEX_WT:-/workspace/.worktrees/$name}"  # CODEX_WT で別のリポジトリの worktree も（YoluPainter-rs など）
[[ -d "$WT" ]] || { echo "worktree が無い: $WT" >&2; exit 2; }
A="$WT/.agent"; mkdir -p "$A"
MODEL="${CODEX_MODEL:-gpt-6.1-sol}"; EFFORT="${CODEX_EFFORT:-xhigh}"; SANDBOX=(--approve-for-me); action=start; RESUME=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --model) MODEL="${2:?}"; shift 2 ;;
    --effort) EFFORT="${2:?}"; shift 2 ;;
    --bypass) SANDBOX=(--dangerously-bypass-approvals-and-sandbox); shift ;;
    --status) action=status; shift ;;
    --stop) action=stop; shift ;;
    --resume) RESUME="${2:?--resume に伝えることが要る}"; shift 2 ;;
    *) echo "知らない引数: $1" >&2; exit 2 ;;
  esac
done
alive() { [[ -f "$A/codex.pid" ]] && kill -0 "$(cat "$A/codex.pid")" 2>/dev/null; }
case "$action" in
  status)
    if alive; then echo "動いている（PID $(cat "$A/codex.pid")）"; else echo "止まっている"; fi
    [[ -f "$A/codex-last.md" ]] && { echo "── 最後の返答 ──"; tail -40 "$A/codex-last.md"; }
    [[ -f "$A/progress.md" ]] && { echo "── 進捗メモ ──"; head -20 "$A/progress.md"; }
    exit 0 ;;
  stop)
    alive && { kill "$(cat "$A/codex.pid")"; echo "止めた"; } || echo "動いていない"
    exit 0 ;;
esac
[[ -f "$A/task.md" ]] || { echo "依頼文が無い: $A/task.md" >&2; exit 2; }
alive && { echo "もう動いている（PID $(cat "$A/codex.pid")）" >&2; exit 1; }
command -v codex >/dev/null || { echo "codex が無い（npm install -g @openai/codex）" >&2; exit 2; }
codex login status >/dev/null 2>&1 || { echo "codex にログインしていない（ユーザーに ! codex login --device-auth を頼む）" >&2; exit 4; }
export AGENT_BOARD_TOKEN_CODEX="$(cat "$HOME/.local/share/agent-board/codex.token" 2>/dev/null || true)"
model_args=(); [[ -n "$MODEL" ]] && model_args=(-m "$MODEL")
prompt="あなたの依頼文は $A/task.md にあります。まずそれと、/workspace/AGENTS.md・/workspace/CLAUDE.md・/workspace/temp~/dev/ORCHESTRATION.md を読み、依頼文の指示どおりに作業してください（作業場所は $WT だけ）。返答・コミットメッセージ・進捗メモは日本語で。最後の返答は依頼文の「報告」の形で。"
mkdir -p "$HOME/.cache/yolupainter-tests"
# push させない: VS Code が渡す資格情報（IPC・askpass）を Codex のプロセスに渡さず、git の credential.helper を空にし、origin への
# push の宛先を無効にする（Codex のプロセスと子にだけ効く。わざと回避されれば防げないが、うっかりの push は通らない）
scrub=(env -u REMOTE_CONTAINERS_IPC -u GIT_ASKPASS -u VSCODE_GIT_ASKPASS_NODE -u VSCODE_GIT_ASKPASS_EXTRA_ARGS -u VSCODE_GIT_ASKPASS_MAIN
  -u VSCODE_GIT_IPC_HANDLE -u SSH_AUTH_SOCK -u GH_TOKEN -u GITHUB_TOKEN GIT_TERMINAL_PROMPT=0
  GIT_CONFIG_COUNT=2 GIT_CONFIG_KEY_0=credential.helper GIT_CONFIG_VALUE_0= GIT_CONFIG_KEY_1=remote.origin.pushurl GIT_CONFIG_VALUE_1=/nonexistent/push-is-for-the-coordinator)
resume_args=()
if [[ -n "$RESUME" ]]; then
  sid=$(grep -a -m1 "^session id:" "$A/codex.log" 2>/dev/null | awk '{print $3}')
  [[ -n "$sid" ]] || { echo "前のセッションの ID が $A/codex.log に無い（--resume なしで始め直す）" >&2; exit 2; }
  n=1; while [[ -f "$A/codex.log.$n" ]]; do n=$((n + 1)); done; mv "$A/codex.log" "$A/codex.log.$n"
  # exec resume には -C と --approve-for-me が無いので、作業フォルダへ移り、自動の審査は設定で渡す（--approve-for-me と同じ中身）
  resume_args=(resume "$sid"); prompt="$RESUME"
  if [[ "${SANDBOX[0]}" == --approve-for-me ]]; then
    SANDBOX=(-c 'approval_policy="on-request"' -c 'approvals_reviewer="auto_review"' -c 'sandbox_mode="workspace-write"')
  fi
  cd "$WT"
  cd_args=()
else
  cd_args=(-C "$WT")
fi
nohup setsid "${scrub[@]}" codex exec ${resume_args[@]+"${resume_args[@]}"} ${cd_args[@]+"${cd_args[@]}"} ${model_args[@]+"${model_args[@]}"} -c "model_reasoning_effort=\"$EFFORT\"" "${SANDBOX[@]}" \
  -o "$A/codex-last.md" "$prompt" > "$A/codex.log" 2>&1 < /dev/null &
echo $! > "$A/codex.pid"
echo "始めた（PID $!、ログ $A/codex.log、モデル ${MODEL:-既定}・推論 $EFFORT）"
