# shellcheck shell=bash
# 常駐 Unity の依頼・同期・台の操作を 1 本ずつ通すロック。呼ぶ側が DAEMON_DIR を定義して source する。
# DAEMON_DIR が定義済みであること。
#
# - client.lock: 依頼の受け口（request.json / done / snippet-in.cs）は 1 組しか無いので、依頼の間は flock で持つ。
#   スクリプトが終わると外れる。
# - switching: switch-daemon.sh がデーモンを止めて別のモードで起動し直している間だけ置かれる印。依頼する側は
#   これが消えるまで待つ（デーモンがいない一瞬にコールド実行へ落ちて、起動中のデーモンとプロジェクトを
#   取り合わないように）。切り替えの手順自身は YOLUPAINTER_DAEMON_SWITCHING=1 で印もロックも素通りする。

# 環境変数だけでは継承を信じない。fd の対象と flock を確かめ、閉じた fd や別の台の fd は使わない。
daemon_client_lock_held() {
  [[ /proc/$BASHPID/fd/8 -ef "$DAEMON_DIR/client.lock" ]] && flock -n 8
}

acquire_daemon_client_lock() {
  [[ -n "${YOLUPAINTER_DAEMON_SWITCHING:-}" ]] && return 0
  # 台を選んだ親や再起動を頼んだ親の、同じ台の fd 8 ならそのまま使う。
  daemon_client_lock_held && return 0
  mkdir -p "$DAEMON_DIR"
  local waited=0
  while :; do
    while [[ -f "$DAEMON_DIR/switching" ]]; do
      [[ $waited == 0 ]] && echo "==> テストデーモンの切り替えを待っている…" >&2
      waited=1; sleep 2
    done
    exec 8>"$DAEMON_DIR/client.lock"
    flock 8
    # 待っている間に切り替えが始まったら、いったん放して切り替えを先に通す
    [[ -f "$DAEMON_DIR/switching" ]] || return 0
    exec 8>&-
  done
}

# 子プロセス（特に常駐させる Unity）にロックのファイルを引き継がせない。引き継ぐと、その Unity が
# 生きている間ずっとロックが外れず、誰の依頼も通らなくなる（2026-10-02 に実際に起きた）。
close_inherited_daemon_lock() {
  local fd target pid=$BASHPID
  for fd in /proc/$pid/fd/*; do
    target="$(readlink "$fd" 2>/dev/null)" || continue
    [[ "$target" == *"/TestDaemon/client.lock" ]] && eval "exec ${fd##*/}>&-"
  done
  return 0
}
