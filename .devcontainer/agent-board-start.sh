#!/usr/bin/env bash
# devcontainer の起動ごとに、エージェントの掲示板（別リポジトリ agent-board）を立ち上げる（postStartCommand）。
# - ~/agent-board が無ければ（コンテナを作り直した後）GitHub から取る。
# - Claude Code 用のトークン（~/.local/share/agent-board/claude.token。~/.zshrc の AGENT_BOARD_TOKEN が読む）が無ければ発行し直す。
#   人が画面に入るトークンは、作り直した後は `~/agent-board/bin/agent-board token issue user --kind human` で発行し直す。
# どれかに失敗しても、コンテナの起動は止めない。
set -u
repo_url="https://github.com/YozoraKurage/agent-board.git"
dir="$HOME/agent-board"
home="${AGENT_BOARD_HOME:-$HOME/.local/share/agent-board}"
if [ ! -d "$dir/.git" ]; then
  git clone -q "$repo_url" "$dir" 2>/dev/null || { echo "agent-board: 取ってこられなかった（$repo_url）。あとで手で clone する"; exit 0; }
fi
"$dir/bin/agent-boardctl" start || { echo "agent-board: 起動できなかった（$dir/bin/agent-boardctl log で確かめる）"; exit 0; }
if [ ! -s "$home/claude.token" ]; then
  ( umask 077; "$dir/bin/agent-board" token issue claude 2>/dev/null | grep -o '[A-Za-z0-9_-]\{24,\}' | head -1 > "$home/claude.token" )
  [ -s "$home/claude.token" ] && echo "agent-board: Claude Code 用のトークンを発行し直した（新しいターミナルから効く）"
fi
exit 0
