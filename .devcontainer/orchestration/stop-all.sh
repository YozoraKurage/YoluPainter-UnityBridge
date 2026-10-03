#!/usr/bin/env bash
# コンテナを止める・作り直す前に、テストの台と掲示板を行儀よく止める（エージェントへの一時停止の指示の後で使う）。
#   stop-all.sh          worktree に書き残しの無いもの（進捗メモが 30 分以上前・未コミットの変更）があれば一覧を出して止まる
#   stop-all.sh --force  一覧を出したうえで止める
# worktree と未コミットの変更は /workspace（ホストのディスク）にあるので、止めても作り直しても消えない。消えるのは
# エージェントの頭の中だけなので、止める前に各エージェントに .agent/progress.md と wip のコミットを書かせること（ORCHESTRATION.md）。
set -u
force=0; [ "${1:-}" = --force ] && force=1
cd /workspace || exit 1
stale=()
while read -r wt; do
  [ "$wt" = /workspace ] && continue
  case "$wt" in */scratchpad/*) continue ;; esac
  prog="$wt/.agent/progress.md"; dirty="$(git -C "$wt" status --porcelain 2>/dev/null | grep -v '^?? .agent/' | wc -l)"
  if [ ! -f "$prog" ]; then stale+=("$wt: 進捗メモが無い（未コミット $dirty）")
  elif [ $(( $(date +%s) - $(date -r "$prog" +%s) )) -gt 1800 ]; then stale+=("$wt: 進捗メモが $(( ($(date +%s) - $(date -r "$prog" +%s)) / 60 )) 分前（未コミット $dirty）")
  fi
done < <(git worktree list --porcelain | awk '/^worktree /{print $2}')
if [ ${#stale[@]} -gt 0 ]; then
  printf '書き残しが古いか無い worktree:\n'; printf '  %s\n' "${stale[@]}"
  [ $force = 1 ] || { echo "エージェントに一時停止を指示して書き残させてから、もう一度（そのままでよければ --force）"; exit 1; }
fi
echo "テストの台を止める…"
/workspace/.devcontainer/unity/runners.sh stop >/dev/null 2>&1
/workspace/.devcontainer/unity/test-daemon.sh stop >/dev/null 2>&1
echo "掲示板を止める…"
~/agent-board/bin/agent-boardctl stop >/dev/null 2>&1
/workspace/.devcontainer/unity/runners.sh status
echo "止めた。再開は /workspace/temp~/dev/ORCHESTRATION.md の「再開するとき」"
