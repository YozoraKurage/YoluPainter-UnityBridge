#!/usr/bin/env bash
# テストデーモンのモードを、ほかの依頼（複数のエージェントなど）と衝突せずに切り替える。
#
#   switch-daemon.sh gui                 # GUI モードへ（ウィンドウ操作のテスト用）
#   switch-daemon.sh batch-gl            # batch-gl へ（シェーダー・GPU のテスト用）
#   switch-daemon.sh gui -- --filter 'Yozolab.YoluPainter.Tests.WindowTests'
#                                        # GUI で run-tests.sh を回してから、元のモード（batch-gl）に戻す
#   switch-daemon.sh gui --              # GUI で全件を回してから batch-gl に戻す
#
# 切り替えの間は TestDaemon/switching を置く。run-tests.sh / unity-do.sh はそれが消えるまで待つので、
# デーモンがいない一瞬にコールド実行へ落ちない。実行中の依頼があれば、それが終わってから切り替える。
set -uo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
set +e  # common.sh の -e を外す: テストが落ちても、元のモードへ戻す手順まで必ず進む

readonly DAEMON_DIR="$UNITY_PROJECT/TestDaemon"
mode="${1:-}"; shift || true
[[ "$mode" == gui || "$mode" == batch-gl ]] || die "使い方: switch-daemon.sh gui|batch-gl [-- run-tests.sh の引数]"
run_args=(); run_tests=0
# -- があれば試験を回す（-- の後が空なら全件。-- の後に何も渡さず「切り替えだけ」になる取り違えが続いたため）
if [[ "${1:-}" == "--" ]]; then shift; run_args=("$@"); run_tests=1; fi

mkdir -p "$DAEMON_DIR"
# 切り替えどうしは 1 本ずつ通す（2 本が同時に止めて起動すると、2 つ目の Unity が「同じプロジェクトを別の Unity が開いている」で
# 落ち、生きている 1 つ目は pid の記録が無いまま残った。2026-10-03）。後から来た切り替えは、前の切り替え（とその試験・戻し）が
# 終わってから自分のモードにする。このロックは起動する Unity に引き継がせない（下の 9>&-）。
exec 9>"$DAEMON_DIR/switch.lock"
flock 9
touch "$DAEMON_DIR/switching"
trap 'rm -f "$DAEMON_DIR/switching"' EXIT
export YOLUPAINTER_DAEMON_SWITCHING=1
# 実行中の依頼が終わるのを待つ（終わったらすぐ放す。新しい依頼は switching の印で待っている）
flock "$DAEMON_DIR/client.lock" true

start_mode() {
  "$SCRIPT_DIR/test-daemon.sh" stop >/dev/null 2>&1 9>&- || true
  if [[ "$1" == gui ]]; then "$SCRIPT_DIR/test-daemon.sh" start 9>&-; else "$SCRIPT_DIR/test-daemon.sh" start --batch-gl 9>&-; fi
}
start_mode "$mode" 2>&1 | tail -2
code=0
if [[ $run_tests == 1 ]]; then
  "$SCRIPT_DIR/run-tests.sh" ${run_args[@]+"${run_args[@]}"} 9>&-; code=$?
  # GUI での試験が済んだら、ほかの依頼が普段使う batch-gl に戻す
  [[ "$mode" == gui ]] && start_mode batch-gl 2>&1 | tail -2
fi
exit $code
