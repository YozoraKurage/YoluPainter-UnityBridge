#!/bin/bash
# 遅いテストを探す: run-tests.sh が台ごとに残したテストごとの時間（~/.cache/yolupainter-tests/durations）を集計する。
#
#   test-durations.sh                 # 直近の実行 1 回（台・モードを問わない）
#   test-durations.sh --mode gui      # GUI の台の直近 1 回
#   test-durations.sh --runs 5        # 直近 5 回の、テストごとの中央値
#   test-durations.sh --top 30        # 上位 30 件ずつ（既定 15）
#   test-durations.sh --list          # 残っている記録の一覧
#
# 出力: クラスごとの合計（多い順）と、1 件ずつの時間（長い順）。時間は Unity の Test Runner が測った各テストの
# 実行時間（SetUp・TearDown を含み、OneTimeSetUp は最初の 1 件に入ることがある）。台のあいだの待ち・同期・
# コンパイルは入らない。
set -euo pipefail
HIST="$HOME/.cache/yolupainter-tests/durations"
MODE="" RUNS=1 TOP=15 LIST=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) MODE="${2:?--mode に gui か batch-gl が要る}"; shift 2 ;;
    --runs) RUNS="${2:?--runs に回数が要る}"; shift 2 ;;
    --top)  TOP="${2:?--top に件数が要る}"; shift 2 ;;
    --list) LIST=1; shift ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) echo "知らない引数: $1" >&2; exit 2 ;;
  esac
done
[[ -d "$HIST" ]] || { echo "記録が無い（run-tests.sh でテストを回すと $HIST に残る）" >&2; exit 1; }
if [[ $LIST == 1 ]]; then
  for f in $(ls -1t "$HIST"/*.tsv); do printf '%s  %5d 件  %s\n' "$(basename "$f" .tsv)" "$(grep -vc '^#' "$f")" "$(head -1 "$f" | cut -c3-)"; done
  exit 0
fi
files=$(ls -1t "$HIST"/*"${MODE:+-$MODE}".tsv 2>/dev/null | head -n "$RUNS")
[[ -n "$files" ]] || { echo "記録が無い${MODE:+（モード $MODE）}" >&2; exit 1; }
python3 - "$TOP" $files <<'EOF'
import sys, os, statistics, collections
top = int(sys.argv[1]); files = sys.argv[2:]
per = collections.defaultdict(list)
for f in files:
    for line in open(f, encoding='utf-8'):
        if line.startswith('#'): continue
        parts = line.rstrip('\n').split('\t')
        if len(parts) < 3: continue
        per[parts[0]].append((float(parts[1]), parts[2]))
print('記録: ' + ', '.join(os.path.basename(f)[:-4] for f in files))
cases = {n: statistics.median(d for d, _ in v) for n, v in per.items()}
total = sum(cases.values())
print('テスト %d 件、合計 %.0f 秒%s' % (len(cases), total, '（各テストの中央値の合計）' if len(files) > 1 else ''))
def fixture(name):
    base = name.split('(')[0]
    return base.rsplit('.', 1)[0]
byfix = collections.Counter(); count = collections.Counter()
for n, d in cases.items(): byfix[fixture(n)] += d; count[fixture(n)] += 1
print('\nクラスごと（合計の多い順）:')
for k, v in byfix.most_common(top):
    print('  %7.1f s  %4.1f%%  %4d 件  %s' % (v, 100 * v / total if total else 0, count[k], k.replace('Yozolab.YoluPainter.Tests.', '')))
print('\n1 件ずつ（長い順）:')
for n, d in sorted(cases.items(), key=lambda x: -x[1])[:top]:
    print('  %7.2f s  %s' % (d, n.replace('Yozolab.YoluPainter.Tests.', '')[:150]))
EOF
