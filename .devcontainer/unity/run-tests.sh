#!/usr/bin/env bash
# YoluPainter の EditMode テストを、空いているテストの台（常駐 Unity）で実行する。
#
#   run-tests.sh                                   全件（batch-gl の台）
#   run-tests.sh --filter 'Yozolab.YoluPainter.Tests.(FooTests|BarTests)[.]'   完全名の正規表現（TESTING.md）
#   run-tests.sh --category Slow
#   run-tests.sh --mode gui                        窓の操作の試験は GUI の台で（既定は batch-gl）
#   run-tests.sh --sha HEAD                        作業ツリーではなく、このコミットの木で回す（台 1 以上だけ）
#   run-tests.sh --source /path/to/worktree        このフォルダのパッケージで回す（台 1 以上だけ）
#   run-tests.sh --both [--sha X]                  batch-gl と GUI の台で同時に回し、両方の結果を出す
#   run-tests.sh --runner 2                        台を決めて回す
#   run-tests.sh --log                             失敗時に Unity ログの末尾も出す
#
# 台（runners.conf・runners.sh）: 台 0 はパッケージとして /workspace を直接読む。台 1 以上は依頼のたびに台の中の写しへ
# ソース（既定は /workspace の作業ツリー、--sha ならそのコミット）を同期してから回すので、ほかの作業者がテストの最中に
# 保存しても影響を受けない。空いている台は依頼の受け口のロック（TestDaemon/client.lock）で取り合う（台 1 以上を先に）。
# 動いている台が無ければ、今までどおり台 0 のデーモンかコールド実行に落ちる。
#
# 標準出力にはサマリと失敗内容だけを出す。Unity の生ログ（数万行）は台のプロジェクトの Logs/ に残る。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

FILTER=""
CATEGORY=""
SHOW_LOG=0
MODE=""
RUNNER_ARG=""
SHA=""
SOURCE_DIR=""
BOTH=0
original_args=("$@")

while [[ $# -gt 0 ]]; do
  case "$1" in
    --filter)   FILTER="${2:?--filter に値が要る}"; shift 2 ;;
    --category) CATEGORY="${2:?--category に値が要る}"; shift 2 ;;
    --log)      SHOW_LOG=1; shift ;;
    --mode)     MODE="${2:?--mode に gui か batch-gl が要る}"; shift 2 ;;
    --runner)   RUNNER_ARG="${2:?--runner に台の番号が要る}"; shift 2 ;;
    --sha)      SHA="${2:?--sha にコミットが要る}"; shift 2 ;;
    --source)   SOURCE_DIR="${2:?--source にフォルダが要る}"; shift 2 ;;
    --both)     BOTH=1; shift ;;
    -h|--help)  sed -n '2,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)          die "不明な引数: $1" ;;
  esac
done
[[ -z "$MODE" || "$MODE" == gui || "$MODE" == batch-gl ]] || die "--mode は gui か batch-gl"
[[ -z "$SHA" || -z "$SOURCE_DIR" ]] || die "--sha と --source は一緒に使えない"
[[ -z "$RUNNER_ARG" || "$RUNNER_ARG" =~ ^[0-9]+$ ]] || die "--runner は台の番号"
if [[ -n "$SHA" ]]; then
  SHA="$(git -C "$PACKAGE_ROOT" rev-parse --verify "$SHA^{commit}" 2>/dev/null)" || die "コミットが無い: $SHA"
fi
if [[ -n "$SOURCE_DIR" ]]; then
  SOURCE_DIR="$(cd "$SOURCE_DIR" 2>/dev/null && pwd)" || die "フォルダが無い: $SOURCE_DIR"
  [[ -f "$SOURCE_DIR/package.json" ]] || die "パッケージ（package.json）が無い: $SOURCE_DIR"
fi

# --both: batch-gl と GUI の台で同時に回して、両方の結果を順に出す（どちらかが落ちたら落ちた方の終了コード）
if [[ $BOTH == 1 ]]; then
  [[ -z "$MODE" && -z "$RUNNER_ARG" ]] || die "--both は --mode・--runner と一緒に使えない"
  rest=()
  for a in "${original_args[@]}"; do [[ "$a" == --both ]] || rest+=("$a"); done
  out_dir="$(mktemp -d)"; trap 'rm -rf "$out_dir"' EXIT
  "$0" ${rest[@]+"${rest[@]}"} --mode batch-gl > "$out_dir/batch-gl" 2>&1 & p1=$!
  "$0" ${rest[@]+"${rest[@]}"} --mode gui > "$out_dir/gui" 2>&1 & p2=$!
  # common.sh の set -e の下では、落ちた子の wait でここから抜けて結果を出さずに終わってしまう（全件で実際に起きた）
  c1=0; wait $p1 || c1=$?; c2=0; wait $p2 || c2=$?
  echo "──── batch-gl ────"; cat "$out_dir/batch-gl"
  echo ""; echo "──── GUI ────"; cat "$out_dir/gui"
  [[ $c1 != 0 ]] && exit $c1
  exit $c2
fi

# 台を選ぶ（選んだ台のロックを持ったまま、その台の設定で自分を実行し直す）。switch-daemon.sh の中から呼ばれたときと、
# 台がもう決まって実行し直された後は選ばない。
if [[ -z "${YOLUPAINTER_LOCK_HELD:-}" && -z "${YOLUPAINTER_DAEMON_SWITCHING:-}" ]]; then
  want="${MODE:-batch-gl}"
  candidates=()
  if [[ -n "$RUNNER_ARG" ]]; then candidates=("$RUNNER_ARG")
  else
    for n in $(runner_numbers) 0; do candidates+=("$n"); done
  fi
  eligible=()
  for n in "${candidates[@]}"; do
    live="$(runner_live_mode "$n")"
    [[ "$live" == down ]] && continue
    [[ "$n" == 0 && ( -n "$SHA" || -n "$SOURCE_DIR" ) ]] && continue  # 台 0 は /workspace を直接読むので、コミットやフォルダを選べない
    if [[ -n "$RUNNER_ARG" && -z "$MODE" ]] || [[ "$live" == "$want" ]]; then eligible+=("$n"); fi
  done
  if [[ ${#eligible[@]} -eq 0 ]]; then
    [[ -n "$SHA" || -n "$SOURCE_DIR" ]] && die "--sha・--source で回せる台（1 以上）が動いていない（runners.sh status / runners.sh start）"
    [[ -n "$RUNNER_ARG" ]] && die "台 $RUNNER_ARG は動いていないか、モードが ${want} ではない（runners.sh status）"
    [[ "$want" == gui ]] && die "GUI の台が動いていない（runners.sh start、または switch-daemon.sh gui -- で台 0 を切り替えて回す）"
    : # batch-gl の台が無い: 今までどおり台 0（デーモンかコールド）へ落ちる
  else
    waited=0
    while :; do
      for n in "${eligible[@]}"; do
        dir="$(runner_project "$n")/TestDaemon"; mkdir -p "$dir"
        [[ -f "$dir/switching" ]] && continue
        exec 8>"$dir/client.lock"
        if flock -n 8; then
          if [[ -f "$dir/switching" ]]; then exec 8>&-; continue; fi
          export YOLUPAINTER_RUNNER="$n" YOLUPAINTER_LOCK_HELD=1
          exec "$0" "${original_args[@]}"
        fi
        exec 8>&-
      done
      [[ $waited == 0 ]] && info "空いているテストの台（${eligible[*]}）を待っている…"
      waited=1; sleep 1
    done
  fi
fi

restore_license
have_license || { license_hint; exit 4; }

if [[ ! -f "$UNITY_PROJECT/Packages/manifest.json" ]]; then
  info "テストプロジェクトが未作成。setup.sh を先に実行する"
  "$SCRIPT_DIR/setup.sh"
fi

# デーモン（test-daemon.sh start で常駐させた Unity）が生きていれば、起動費を払わずに
# そちらへ依頼する。死んでいれば黙って従来のコールド実行へ落ちる。
readonly DAEMON_DIR="$UNITY_PROJECT/TestDaemon"
# 死活は PID だけで見る。ハートビートは使わない — 同期的な Refresh や長いテスト
# フレームの間は update が止まって鼓動も止まるので、鮮度で判定すると「忙しい」を
# 「死んだ」と誤読してコールドに落ち、常駐とロック衝突する(実測済み)。
daemon_alive() {
  [[ -f "$DAEMON_DIR/daemon.pid" ]] \
    && kill -0 "$(cat "$DAEMON_DIR/daemon.pid")" 2>/dev/null
}
# 依頼の受け口（request.json / done）は 1 つしか無いので、同時に走る依頼（複数のエージェントや
# unity-do.sh）を 1 本ずつ通す（daemon-lock.sh）。switch-daemon.sh がモードを切り替えている間は、
# コールドへ落ちずに切り替えが終わるのを待つ。
source "$SCRIPT_DIR/daemon-lock.sh"
acquire_daemon_client_lock
if [[ "$UNITY_RUNNER" != 0 ]]; then
  daemon_alive || die "台 $UNITY_RUNNER の常駐 Unity が動いていない（runners.sh start $UNITY_RUNNER）"
  pkg="$(runner_package "$UNITY_RUNNER")"
  if [[ -n "$SHA" ]]; then
    stage="$RUNNERS_HOME/$UNITY_RUNNER/stage"; rm -rf "$stage"; mkdir -p "$stage"
    git -C "$PACKAGE_ROOT" archive "$SHA" | tar -x -C "$stage"
    python3 "$SCRIPT_DIR/sync-package.py" "$stage" "$pkg" --checksum | sed 's/^/    /'
    rm -rf "$stage"; echo "commit $SHA" > "$RUNNERS_HOME/$UNITY_RUNNER/source.txt"
    what="コミット ${SHA:0:12}"
  else
    from="${SOURCE_DIR:-$PACKAGE_ROOT}"
    python3 "$SCRIPT_DIR/sync-package.py" "$from" "$pkg" --checksum | sed 's/^/    /'
    echo "folder $from" > "$RUNNERS_HOME/$UNITY_RUNNER/source.txt"
    what="フォルダ $from"
  fi
  info "台 $UNITY_RUNNER（$(runner_live_mode "$UNITY_RUNNER")）で回す: $what"
fi
if daemon_alive; then
  info "デーモンへ依頼 (台 $UNITY_RUNNER、PID $(cat "$DAEMON_DIR/daemon.pid"))"
  rm -f "$DAEMON_DIR/done" "$DAEMON_DIR/result.xml"
  printf '{"filter":"%s","category":"%s"}' "$FILTER" "$CATEGORY" \
    > "$DAEMON_DIR/request.json"
  # 鼓動が長く止まっていたら、忙しいのではなく固まっている。死活は PID で見る(上の
  # コメントのとおり、忙しい常駐を殺さないため)が、PID は主スレッドが止まった Unity にも
  # 「生きている」と答える — ネイティブのダイアログが出るとそうなり、デーモン自身の
  # 見張りも同じ主スレッドなので code 5 すら返せない(2026-09-18 実測、22 分無音)。
  # 鼓動は 2 秒ごとなので、この閾値は「長いテストフレーム」より十分に長く取る。
  readonly BEAT_STALE=180
  beat_age() {
    local f="$DAEMON_DIR/alive"
    [[ -f "$f" ]] || { echo 99999; return; }
    echo $(( $(date +%s) - $(stat -c %Y "$f") ))
  }
  stalled=0
  for _ in $(seq 1 900); do
    sleep 1
    [[ -f "$DAEMON_DIR/done" ]] && break
    daemon_alive || break
    if [[ $(beat_age) -gt $BEAT_STALE ]]; then stalled=1; break; fi
  done
  if [[ $stalled == 1 ]]; then
    warn "デーモンの鼓動が $(beat_age) 秒止まっている（主スレッドごと固まっている）。test-daemon.sh restart を"
    exit 5
  fi
  if [[ ! -f "$DAEMON_DIR/done" ]] && daemon_alive; then
    # 生きているのに 15 分応答が無い。止まった実行はデーモン自身が 1〜2 分で code 5 を返すので、
    # ここに来るのはそれすら回らない状態。全件は正当に長い — GUI 常駐ではテストが起こす
    # ドメインリロードで実行がやり直され、実測 8 分超になった(2026-09-16)。短くしない。
    # 勝手に殺してコールドへ落ちると常駐とロック衝突するので、ここでは状況を言って止まるだけ。
    warn "デーモンは生きているが 15 分応答が無い。test-daemon.sh restart を検討 (ログ: $UNITY_LOG_DIR/daemon.log)"
    exit 1
  fi
  if [[ -f "$DAEMON_DIR/done" ]]; then
    code=$(head -1 "$DAEMON_DIR/done")
    if [[ "$code" == 3 ]]; then
      warn "デーモン側でコンパイルエラー: $(sed -n 2p "$DAEMON_DIR/done")"
      grep -o '[^ ]*\.cs([0-9]*,[0-9]*): error CS[0-9]*: .*' \
        "$UNITY_LOG_DIR/daemon.log" 2>/dev/null | sort -u | head -50
      exit 3
    fi
    if [[ "$code" == 5 ]]; then
      warn "デーモンが止まっていた: $(sed -n 2p "$DAEMON_DIR/done") — $DAEMON_DIR/trace.log を見てから test-daemon.sh restart を"
      exit 5
    fi
    echo ""
    node "$SCRIPT_DIR/summarize-results.js" "$DAEMON_DIR/result.xml" || true
    exit "$code"
  fi
  warn "デーモンのプロセスが死んでいた。後始末してコールドで続行する"
  rm -f "$DAEMON_DIR/daemon.pid" "$DAEMON_DIR/running.json" "$DAEMON_DIR/request.json"
fi

mkdir -p "$UNITY_LOG_DIR"
readonly LOG="$UNITY_LOG_DIR/tests.log"
readonly RESULTS="$UNITY_LOG_DIR/test-results.xml"
rm -f "$RESULTS"

args=(
  -nographics
  -projectPath "$UNITY_PROJECT"
  -logFile "$LOG"
  -runTests
  -testPlatform EditMode
  -testResults "$RESULTS"
)
[[ -n "$FILTER"   ]] && args+=(-testFilter "$FILTER")
[[ -n "$CATEGORY" ]] && args+=(-testCategory "$CATEGORY")

run_unity() {
  set +e
  "$UNITY_EDITOR" "${args[@]}"
  unity_status=$?
  set -e
}

info "実行中… (ログ: $LOG)"
run_unity

# パッケージを足した直後などは、Unity がまだコンパイルを終えていないうちにテストが
# 始まることがある。そうなると AddStateMachineBehaviour が黙って null を返し、
# 何十件もの無関係な NullReference になって出てくる。原因はログのこの一行だけ。
# 一度通せば Library が温まって解消するので、黙って一回やり直す。
if grep -q "Please fix compile errors" "$LOG" 2>/dev/null; then
  warn "コンパイルが終わらないうちにテストが走った。インポートを通してからやり直す"
  "$UNITY_EDITOR" -nographics -projectPath "$UNITY_PROJECT" \
    -logFile "$UNITY_LOG_DIR/import.log" -quit || true
  rm -f "$RESULTS"
  run_unity
fi

# コンパイルエラーだと結果 XML すら出ない。その場合はログから CS エラーだけ拾う。
if [[ ! -f "$RESULTS" ]]; then
  warn "結果 XML が出力されなかった (Unity 終了コード: $unity_status)"
  if grep -q 'error CS' "$LOG" 2>/dev/null; then
    echo ""
    echo "コンパイルエラー:"
    grep -o '[^ ]*\.cs([0-9]*,[0-9]*): error CS[0-9]*: .*' "$LOG" | sort -u | head -50
  else
    tail -40 "$LOG" >&2
  fi
  exit 3
fi

echo ""
set +e
node "$SCRIPT_DIR/summarize-results.js" "$RESULTS"
summary_status=$?
set -e

if [[ $summary_status -ne 0 || $unity_status -ne 0 ]]; then
  echo ""
  info "生ログ: $LOG   結果 XML: $RESULTS"
  [[ $SHOW_LOG -eq 1 ]] && { echo ""; tail -60 "$LOG"; }
  exit 1
fi

exit 0
