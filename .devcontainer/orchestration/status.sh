#!/usr/bin/env bash
# エージェントの作業の状態をまとめて見る（指揮役が、区切り・止める前・再開した後に使う）。
#   status.sh        worktree ごとのブランチ・統合の作業ブランチ（/workspace の今のブランチ）より先のコミット・未コミットの数・進捗メモ（.agent/progress.md）の頭、
#                    テストの台、掲示板
set -u
INTEG="$(git -C /workspace branch --show-current)"  # 統合の作業ブランチ（次に出す版の名前。0.1.0 の後は 0.2.0）
cd /workspace || exit 1
echo "== 統合の木 /workspace [$(git branch --show-current) $(git rev-parse --short HEAD)]  未コミット $(git status --porcelain | wc -l)"
git worktree list --porcelain | awk '/^worktree /{print $2}' | while read -r wt; do
  [ "$wt" = /workspace ] && continue
  case "$wt" in */scratchpad/*) continue ;; esac
  br="$(git -C "$wt" branch --show-current 2>/dev/null)"
  ahead="$(git rev-list --count "$INTEG".."${br:-HEAD}" 2>/dev/null || echo '?')"
  dirty="$(git -C "$wt" status --porcelain 2>/dev/null | wc -l)"
  prog="$wt/.agent/progress.md"
  when="$( [ -f "$prog" ] && date -r "$prog" '+%m-%d %H:%M' || echo 'なし' )"
  echo "== $wt [${br:-?}] $INTEG より +$ahead コミット、未コミット $dirty、進捗メモ $when"
  [ -f "$prog" ] && sed -n '1,8p' "$prog" | sed 's/^/    /'
done
echo
/workspace/.devcontainer/unity/runners.sh status 2>/dev/null
echo
~/agent-board/bin/agent-boardctl status 2>/dev/null || echo "掲示板: 動いていない"
