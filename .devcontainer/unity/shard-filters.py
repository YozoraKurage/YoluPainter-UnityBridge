#!/usr/bin/env python3
"""全件のテストを N 組に分ける絞り込み（完全名の正規表現）を、組ごとに 1 行ずつ出す（run-tests.sh --shards）。

  shard-filters.py <組の数> <all|gui-only> <テストのソースのフォルダ（Tests/Editor）> <GuiOnlyFixtures.txt> <時間の履歴のフォルダ>

- 単位はテストのクラス（ソースの class 宣言から取るので、履歴に無い新しいクラスも入る）。gui-only は GuiOnlyFixtures.txt の
  クラスだけで、1 つで全体の 1/(2N) を超えるクラス（WindowTests）は、メソッド名の頭の 1 文字ごとに分ける（頭の文字は
  [A-Za-z0-9_] を全部どこかの組に入れるので、新しいメソッドも漏れない）。
- 重さは履歴（run-tests.sh が残したテストごとの時間の、新しい 30 回分の中央値）。履歴に無い単位は 1 秒と見なす。
- 重い順に、いちばん軽い組へ入れる（LPT）。
- all のときは、最後の組に「既知のクラスのどれでもない完全名」を拾う受け皿（否定の先読み）を付け、ソースから取りこぼした
  クラス（入れ子の名前空間など）も必ずどこかで回るようにする。
- 正規表現は run-tests.sh の決まり（点は [.]、バックスラッシュと二重引用符を使わない）に従う。
"""
import glob
import os
import re
import statistics
import string
import sys
from collections import defaultdict

PREFIX = "Yozolab.YoluPainter.Tests."
RX_PREFIX = "Yozolab[.]YoluPainter[.]Tests[.]"
FIRST_CHARS = string.ascii_uppercase + string.ascii_lowercase + string.digits + "_"


def source_units(tests_dir):
    """ソースから単位（クラス名、または入れ子の名前空間の最初の段）を集める。"""
    units = set()
    for path in glob.glob(os.path.join(tests_dir, "**", "*.cs"), recursive=True):
        text = open(path, encoding="utf-8", errors="replace").read()
        ns = re.search(r"^\s*namespace\s+([A-Za-z0-9_.]+)", text, re.M)
        if not ns or not (ns.group(1) + ".").startswith(PREFIX):
            continue
        rest = ns.group(1)[len(PREFIX):] if len(ns.group(1)) > len(PREFIX) - 1 else ""
        if rest:
            units.add(rest.split(".")[0])
            continue
        # 入れ子でないクラス（字下げ 4 つまで）の名前
        for m in re.finditer(r"^\s{0,4}(?:public |internal |sealed |static |partial |abstract )*class\s+([A-Za-z0-9_]+)", text, re.M):
            units.add(m.group(1))
    return units


def unit_of(fullname):
    """完全名 → (クラス, メソッド)。パラメータ付きのフィクスチャ Class(Arg).Method も扱う。"""
    if not fullname.startswith(PREFIX):
        return None, None
    rest = fullname[len(PREFIX):]
    m = re.match(r"([A-Za-z0-9_]+)(?:\([^)]*\))?\.(.*)", rest)
    if not m:
        return rest, ""
    return m.group(1), m.group(2)


def history(hist_dir):
    files = sorted(glob.glob(os.path.join(hist_dir, "*.tsv")), key=os.path.getmtime, reverse=True)[:30]
    samples = defaultdict(list)
    for f in files:
        for line in open(f, encoding="utf-8"):
            if line.startswith("#"):
                continue
            parts = line.rstrip("\n").split("\t")
            # 飛ばした（Skipped・Ignored）テストの時間は 0 に近く、別のモードの台での重さにならないので数えない
            if len(parts) >= 3 and parts[2] in ("Passed", "Failed"):
                try:
                    samples[parts[0]].append(float(parts[1]))
                except ValueError:
                    pass
    return {name: statistics.median(v) for name, v in samples.items()}


def main(argv):
    if len(argv) != 5:
        print(__doc__, file=sys.stderr)
        return 2
    n, kind, tests_dir, gui_list, hist_dir = int(argv[0]), argv[1], argv[2], argv[3], argv[4]
    gui = [l.strip() for l in open(gui_list, encoding="utf-8") if l.strip() and not l.strip().startswith("#")] if os.path.isfile(gui_list) else []
    times = history(hist_dir) if os.path.isdir(hist_dir) else {}

    if kind == "gui-only":
        classes = set(gui)
    elif kind == "all":
        classes = source_units(tests_dir) | {c for c in (unit_of(t)[0] for t in times) if c}
    else:
        print("2 つ目は all か gui-only", file=sys.stderr)
        return 2

    class_time = defaultdict(float)
    first_time = defaultdict(float)  # (クラス, 頭の文字) → 秒
    for name, d in times.items():
        cls, meth = unit_of(name)
        if cls in classes:
            class_time[cls] += d
            if meth:
                first_time[(cls, meth[0])] += d
    if kind == "all":
        for c in gui:  # batch-gl の台ではすぐ飛ばされる（重さは GUI の台でのもの）
            if c in class_time:
                class_time[c] = 0.1
    total = sum(class_time.get(c, 1.0) for c in classes) or 1.0

    units = []  # (秒, 正規表現の断片)
    for cls in sorted(classes):
        t = class_time.get(cls, 1.0)
        if n > 1 and t > total / (2 * n):
            for ch in FIRST_CHARS:
                units.append((first_time.get((cls, ch), 0.0), "%s[.]%s" % (cls, ch)))
        else:
            units.append((t, "%s[.(]" % cls))

    shards = [[0.0, []] for _ in range(max(1, n))]
    for t, frag in sorted(units, key=lambda u: (-u[0], u[1])):
        lightest = min(shards, key=lambda s: s[0])
        lightest[0] += t
        lightest[1].append(frag)

    if kind == "all":
        # 絞り込みはテストの完全名だけでなく、組（アセンブリ Yozolab.YoluPainter.Tests.dll・名前空間・クラス）の名前にも当てられ、
        # 組に当たると中の全部が回る。受け皿がアセンブリや既知の名前空間に当たらないよう、名前の終わり（$）も既知として除く
        known = "|".join("%s(?:[.(]|$)" % c for c in sorted(classes))
        shards[-1][1].append("(?!(?:%s|dll$))" % known)

    for load, frags in shards:
        if not frags:
            continue
        print("%.0f\t%s(?:%s)" % (load, RX_PREFIX, "|".join(frags)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
