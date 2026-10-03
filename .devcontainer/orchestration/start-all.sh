#!/usr/bin/env bash
# devcontainer の起動ごと（postStartCommand）に、掲示板とテストの台を立ち上げる。止めたとき・異常終了したときの再開にも使える。
# 掲示板はすぐ（agent-board-start.sh）、テストの台（台 0 は batch-gl、台 1 以上は runners.conf のモード。作り直した後で無ければ
# runners.sh setup で作る）は裏で 1 台ずつ起動する（数分。ログは ~/unity-runners/start-all.log）。どれかに失敗しても起動は止めない。
set -u
bash /workspace/.devcontainer/agent-board-start.sh
mkdir -p "$HOME/unity-runners"
nohup bash -c '
  set -u; u=/workspace/.devcontainer/unity
  echo "== $(date "+%F %T") テストの台を起動する"
  "$u/test-daemon.sh" status | grep -q 起動中 || "$u/test-daemon.sh" start --batch-gl
  for n in $("$u/runners.sh" status | sed -n "s/^台 \([1-9][0-9]*\):.*/\1/p"); do
    [ -f "$HOME/unity-runners/$n/project/Packages/manifest.json" ] || "$u/runners.sh" setup "$n"
    "$u/runners.sh" status | grep -q "^台 $n: down" && "$u/runners.sh" start "$n"
  done
  "$u/runners.sh" status
' > "$HOME/unity-runners/start-all.log" 2>&1 &
echo "テストの台を裏で起動している（ログ: ~/unity-runners/start-all.log）"
exit 0
