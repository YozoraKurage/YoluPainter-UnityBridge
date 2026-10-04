#!/usr/bin/env bash
# テストの台（runner）を作り、常駐させる。台 0 は今までのテストプロジェクト（test-daemon.sh / switch-daemon.sh）。
#
#   runners.sh setup [番号...]     台を作る（既定: runners.conf の全部）。プロジェクトの設定とパッケージを台 0 から写し、
#                                  パッケージの写し（pkg）を HEAD から作って、コールドで 1 回取り込む（初回は数分）
#   runners.sh start [番号...]     常駐させる（モードは runners.conf）
#   runners.sh stop [番号...]
#   runners.sh restart [番号...]
#   runners.sh status              台 0 と全部の台のモード・状態・パッケージの写しの出どころ
#
# 台 1 以上のテストプロジェクトは $RUNNERS_HOME（既定 ~/unity-runners）に置く。名前付きボリュームではないので、
# コンテナを作り直すと消える（runners.sh setup で作り直す。Library は台 0 から写さない: 開いている Unity の
# Library を写すと、データベースが壊れた写しになり得るため）。
# 台 0 の Assets は写さない（解析のために置いたユーザーのアセット ZZ_UserAssets も。CLAUDE.md の決まり）。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

numbers=()
cmd="${1:-status}"; shift || true
if [[ $# -gt 0 ]]; then numbers=("$@"); else mapfile -t numbers < <(runner_numbers); fi
for n in "${numbers[@]}"; do [[ "$n" =~ ^[1-9][0-9]*$ ]] || die "台の番号は 1 以上（台 0 は test-daemon.sh で扱う）: $n"; done

source_project="$(runner_project 0)"
source "$SCRIPT_DIR/daemon-lock.sh"

# 台ごとに、同期・停止・起動の準備が終わるまで同じ依頼ロックを持つ。
# 裏の再起動では親からの fd 8 を使い、別の台なら取り直す。
with_runner_lock() (
  local n="$1"; shift
  readonly DAEMON_DIR="$(runner_project "$n")/TestDaemon"
  acquire_daemon_client_lock
  export YOLUPAINTER_RUNNER="$n" YOLUPAINTER_LOCK_HELD=1
  "$@" "$n"
)

restart_one() { stop_one "$1"; start_one "$1"; }

# パッケージの写しを、/workspace のコミット（既定 HEAD）と同じ中身にする
sync_from_commit() {
  local n="$1" rev="${2:-HEAD}" sha stage
  sha="$(git -C "$PACKAGE_ROOT" rev-parse --verify "$rev^{commit}")" || die "コミットが無い: $rev"
  stage="$RUNNERS_HOME/$n/stage"
  rm -rf "$stage"; mkdir -p "$stage"
  git -C "$PACKAGE_ROOT" archive "$sha" | tar -x -C "$stage"
  python3 "$SCRIPT_DIR/sync-package.py" "$stage" "$(runner_package "$n")" --checksum
  rm -rf "$stage"
  echo "commit $sha" > "$RUNNERS_HOME/$n/source.txt"
}

setup_one() {
  local n="$1" project pkg
  project="$(runner_project "$n")"; pkg="$(runner_package "$n")"
  [[ "$(runner_live_mode "$n")" == down ]] || die "台 $n は動いている。先に runners.sh stop $n"
  [[ -f "$source_project/Packages/manifest.json" ]] || die "台 0 のテストプロジェクトが無い。先に setup.sh"
  info "台 $n を作る: $project"
  mkdir -p "$project/Assets" "$project/Packages" "$pkg"
  # 設定とパッケージの一覧（VPM で入れた lilToon などの埋め込みパッケージも）。Library は写さない
  cp -a "$source_project/ProjectSettings" "$project/"
  [[ -d "$source_project/UserSettings" ]] && cp -a "$source_project/UserSettings" "$project/"
  local item
  for item in "$source_project"/Packages/*; do
    case "$(basename "$item")" in manifest.json|packages-lock.json) continue ;; esac
    rm -rf "$project/Packages/$(basename "$item")"; cp -a "$item" "$project/Packages/"
  done
  # Assets は写さない（テストが作って片付けるもの、解析のために置いたユーザーのアセット ZZ_UserAssets、デーモンの受け口しか無い。
  # 受け口は test-daemon.sh start が入れる）
  # パッケージは台の中の写しを読む
  sed "s#\"file:$PACKAGE_ROOT\"#\"file:$pkg\"#" "$source_project/Packages/manifest.json" > "$project/Packages/manifest.json"
  grep -q "\"file:$pkg\"" "$project/Packages/manifest.json" || die "manifest の YoluPainter の参照を書き換えられなかった"
  sync_from_commit "$n" HEAD
  mkdir -p "$project/Logs"
  info "台 $n をコールドで取り込む（初回は数分。ログ: $project/Logs/setup.log）"
  restore_license
  have_license || { license_hint; exit 4; }
  if ! (
    close_inherited_daemon_lock
    unset YOLUPAINTER_LOCK_HELD YOLUPAINTER_DAEMON_SWITCHING
    exec "$UNITY_EDITOR" -nographics -projectPath "$project" -logFile "$project/Logs/setup.log" -quit
  ); then
    grep -o '[^ ]*\.cs([0-9]*,[0-9]*): error CS[0-9]*: .*' "$project/Logs/setup.log" 2>/dev/null | sort -u | head -20 >&2
    die "台 $n の取り込みに失敗した（$project/Logs/setup.log）"
  fi
  info "台 $n を作った"
}

start_one() {
  local n="$1" mode
  mode="$(runner_conf_mode "$n")"; [[ -n "$mode" ]] || die "runners.conf に台 $n が無い"
  [[ -f "$(runner_project "$n")/Packages/manifest.json" ]] || die "台 $n はまだ無い。先に runners.sh setup $n"
  local flag=""; [[ "$mode" == batch-gl ]] && flag=--batch-gl; [[ "$mode" == batch ]] && flag=--batch
  YOLUPAINTER_RUNNER="$n" "$SCRIPT_DIR/test-daemon.sh" start $flag || return $?
  # 全台で 1 つの記録常駐。二重起動は flock が防ぐ。Unity の生死には触れない。
  if [[ -e /dev/dxg && -x "$SCRIPT_DIR/gpu-memory-daemon.sh" ]]; then
    "$SCRIPT_DIR/gpu-memory-daemon.sh" start || warn "GPU メモリの記録を起動できなかった"
  fi
}

stop_one() { YOLUPAINTER_RUNNER="$1" "$SCRIPT_DIR/test-daemon.sh" stop; }

status_all() {
  local n mode src
  for n in 0 $(runner_numbers); do
    mode="$(runner_live_mode "$n")"
    src="作業ツリー /workspace を直接"
    [[ "$n" != 0 ]] && src="$(cat "$RUNNERS_HOME/$n/source.txt" 2>/dev/null || echo '（まだ無い）')"
    printf '台 %s: %-8s %s  パッケージ: %s\n' "$n" "$mode" "$( [[ $n == 0 ]] && echo '' || echo "(設定: $(runner_conf_mode "$n"))")" "$src"
  done
}

case "$cmd" in
  setup)   for n in "${numbers[@]}"; do with_runner_lock "$n" setup_one; done ;;
  start)   for n in "${numbers[@]}"; do with_runner_lock "$n" start_one; done ;;  # xvfb-run -a の画面番号の取り合いを避けて 1 台ずつ
  stop)    for n in "${numbers[@]}"; do with_runner_lock "$n" stop_one; done ;;
  restart) for n in "${numbers[@]}"; do with_runner_lock "$n" restart_one; done ;;
  status)  status_all ;;
  -h|--help) sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
  *) die "不明なコマンド: $cmd" ;;
esac
