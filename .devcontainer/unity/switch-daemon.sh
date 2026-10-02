#!/usr/bin/env bash
# テストデーモンのモードを、ほかの依頼（複数のエージェントなど）と衝突せずに切り替える。
#
#   switch-daemon.sh gui                 # GUI モードへ（ウィンドウ操作のテスト用）
#   switch-daemon.sh batch-gl            # batch-gl へ（シェーダー・GPU のテスト用）
#   switch-daemon.sh gui -- --filter 'Yozolab.YoluPainter.Tests.WindowTests'
#                                        # GUI で run-tests.sh を回してから、元のモード（batch-gl）に戻す
#
# 切り替えの間は TestDaemon/switching を置く。run-tests.sh / unity-do.sh はそれが消えるまで待つので、
# デーモンがいない一瞬にコールド実行へ落ちない。実行中の依頼があれば、それが終わってから切り替える。
set -uo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"
set +e  # common.sh の -e を外す: テストが落ちても、元のモードへ戻す手順まで必ず進む

readonly DAEMON_DIR="$UNITY_PROJECT/TestDaemon"
mode="${1:-}"; shift || true
[[ "$mode" == gui || "$mode" == batch-gl ]] || die "使い方: switch-daemon.sh gui|batch-gl [-- run-tests.sh の引数]"
run_args=()
if [[ "${1:-}" == "--" ]]; then shift; run_args=("$@"); fi

mkdir -p "$DAEMON_DIR"
touch "$DAEMON_DIR/switching"
trap 'rm -f "$DAEMON_DIR/switching"' EXIT
export YOLUPAINTER_DAEMON_SWITCHING=1
# 実行中の依頼が終わるのを待つ（終わったらすぐ放す。新しい依頼は switching の印で待っている）
flock "$DAEMON_DIR/client.lock" true

start_mode() {
  "$SCRIPT_DIR/test-daemon.sh" stop >/dev/null 2>&1 || true
  if [[ "$1" == gui ]]; then "$SCRIPT_DIR/test-daemon.sh" start; else "$SCRIPT_DIR/test-daemon.sh" start --batch-gl; fi
}
start_mode "$mode" 2>&1 | tail -2
code=0
if [[ ${#run_args[@]} -gt 0 ]]; then
  "$SCRIPT_DIR/run-tests.sh" "${run_args[@]}"; code=$?
  # GUI での試験が済んだら、ほかの依頼が普段使う batch-gl に戻す
  [[ "$mode" == gui ]] && start_mode batch-gl 2>&1 | tail -2
fi
exit $code
