#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""VCC（VPM）で配る zip `<パッケージ名>.<版>.zip` を作る。Python 3 の標準ライブラリと git だけで動く。

    python3 .github/scripts/package.py --out <出力フォルダ>    zip を作る
    python3 .github/scripts/package.py --list                  zip に入る物の一覧だけを出す
    python3 .github/scripts/package.py --bump patch            package.json の version を上げ、新しい版だけを出す
    python3 .github/scripts/package.py --bump-from-labels '["patch"]' --pr 12
                                                               PR のラベル（名前の JSON 配列）から上げ方を 1 つ決めて出す

zip の中身は「git が管理しているファイル」から次の決まりで選ぶ（作業フォルダに残った生成物や、
.gitignore された物は入らない）。すべてのファイルは必ず「入れる」か「入れない」かのどちらかに
決まり、決まらない物があるとここで止まる（黙って落とさない）。

  入れる   トップの INCLUDE_TOP に挙げた物（package.json が zip の根）。
           名前が ~ で終わる Documentation~ も、Unity が取り込まない代わりに使う人が読む物なので、そのまま入れる。
           （Tests を入れるのは、使う人が testables で試験を回せるようにするため。）
  入れない 名前が . で始まる物（.git*・.github・.devcontainer など）、
           ~ で終わるそのほかのフォルダ・ファイル（Documentation~ 以外。トップより下にある物も）。
  止める   上のどちらにも当たらないトップの物。INCLUDE_TOP に足すか、除く理由を決めてから。

中身が同じなら同じバイト列になる（並びは名前の順、時刻と権限は固定、圧縮は同じ設定）。
ただし圧縮の結果は zlib の版に依存するので、同じバイト列を保証するのは同じ Python・zlib の中まで。
VCC の一覧は配られた zip のバイト列から SHA-256 を計算するので、一度出した zip は作り直さない。
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile
import zlib
from collections import Counter

# zip に入れるトップの名前。Unity が取り込むフォルダはそれぞれ <名前>.meta も要る。
INCLUDE_TOP = (
    "package.json", "package.json.meta",
    "README.md", "README.md.meta",
    "licence.md", "licence.md.meta",
    "Editor", "Editor.meta",
    "Tests", "Tests.meta",
    # 名前が ~ で終わるが入れる物（Unity は取り込まない）。
    "Documentation~",   # 使う人向けの文書・第三者の表記（THIRD_PARTY.md）
)

BUMP_KINDS = ("major", "minor", "patch")

# 固定する zip の時刻（zip が表せる最も早い時刻）。中身が同じなら同じバイト列にするため。
ZIP_DATE = (1980, 1, 1, 0, 0, 0)
ZIP_MODE = 0o100644 << 16  # 通常のファイル、rw-r--r--
COMPRESS_LEVEL = 9

NAME_RE = re.compile(r"^[a-z0-9]+(\.[a-z0-9][a-z0-9-]*)+$")
VERSION_RE = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")


class PackageError(Exception):
    """入れる物が決められない・足りない・不揃いのとき。"""


def classify(path):
    """git のパス（/ 区切り）を、(入れるか, 理由) にする。決まらなければ PackageError。"""
    segments = path.split("/")
    if any(s.startswith(".") for s in segments):
        return False, "名前が . で始まる"
    top = segments[0]
    if top in INCLUDE_TOP:
        if any(s.endswith("~") for s in segments[1:]):
            return False, "名前が ~ で終わる"
        return True, ""
    if top.endswith("~"):
        return False, "名前が ~ で終わる"
    raise PackageError("入れるか決まっていない: %s（package.py の INCLUDE_TOP に足すか、入れない理由を決めてください）" % path)


def tracked_files(root):
    """git が管理しているファイルのパス（/ 区切り、名前順）。"""
    out = subprocess.run(["git", "-C", root, "ls-files", "-z"], check=True, stdout=subprocess.PIPE).stdout
    paths = [p.decode("utf-8") for p in out.split(b"\0") if p]
    for p in paths:
        if p.startswith("/") or "\\" in p or ".." in p.split("/"):
            raise PackageError("使えないパス: %r" % p)
    return sorted(set(paths), key=lambda p: p.encode("utf-8"))


def partition(paths):
    """入れる物と、入れない物（(パス, 理由)）に分ける。決まらない物があればまとめて PackageError。"""
    included, excluded, unclassified = [], [], []
    for p in paths:
        try:
            ok, reason = classify(p)
        except PackageError as e:
            unclassified.append(str(e))
            continue
        if ok:
            included.append(p)
        else:
            excluded.append((p, reason))
    if unclassified:
        raise PackageError("\n".join(unclassified[:20]) + ("\n…ほか %d 件" % (len(unclassified) - 20) if len(unclassified) > 20 else ""))
    return included, excluded


def check_files_exist(root, included):
    problems = []
    for p in included:
        full = os.path.join(root, *p.split("/"))
        if os.path.islink(full):
            problems.append("シンボリックリンクは入れられない: " + p)
        elif not os.path.isfile(full):
            problems.append("作業フォルダに無い（またはファイルではない）: " + p)
    if problems:
        raise PackageError("\n".join(problems[:20]))


def check_meta(included):
    """Unity が取り込む範囲（どの階層にも名前が ~ で終わる物を持たない物）の .meta の過不足を調べる。
    欠けた .meta は VCC の利用者の手元で別の GUID で作られ、参照が壊れる。"""
    files = set(included)
    unity = [p for p in included if not any(s.endswith("~") for s in p.split("/"))]
    dirs = set()
    for p in unity:
        parts = p.split("/")
        for i in range(1, len(parts)):
            dirs.add("/".join(parts[:i]))
    problems = []
    for p in unity:
        if p.endswith(".meta"):
            base = p[:-len(".meta")]
            if base not in files and base not in dirs:
                problems.append("対応する物の無い .meta: " + p)
        elif p + ".meta" not in files:
            problems.append(".meta が無いファイル: " + p)
    for d in sorted(dirs):
        if d + ".meta" not in files:
            problems.append(".meta が無いフォルダ: " + d)
    if problems:
        raise PackageError("\n".join(problems[:20]) + ("\n…ほか %d 件" % (len(problems) - 20) if len(problems) > 20 else ""))


def read_manifest(root):
    with open(os.path.join(root, "package.json"), "rb") as f:
        manifest = json.loads(f.read().decode("utf-8"))
    name, version = manifest.get("name"), manifest.get("version")
    if not isinstance(name, str) or not NAME_RE.match(name):
        raise PackageError("package.json の name が使えない形: %r" % (name,))
    if not isinstance(version, str) or not VERSION_RE.match(version):
        raise PackageError("package.json の version が x.y.z の形ではない: %r" % (version,))
    return name, version


def bump_version(root, kind):
    """package.json の version を x.y.z のまま上げ、新しい版を返す。version の 1 行だけを書き換え、
    ほかのバイト（インデント・改行・権限）は変えない。"""
    if kind not in BUMP_KINDS:
        raise PackageError("版の上げ方は major / minor / patch のどれか: %r" % (kind,))
    path = os.path.join(root, "package.json")
    with open(path, "rb") as f:
        raw = f.read()
    text = raw.decode("utf-8")
    _, current = read_manifest(root)
    pattern = re.compile(r'^(\s*"version"\s*:\s*")(%s)(")' % re.escape(current), re.M)
    if len(pattern.findall(text)) != 1:
        raise PackageError('package.json の "version" の行が 1 つに決まらない')
    major, minor, patch = (int(n) for n in current.split("."))
    new = {"major": (major + 1, 0, 0), "minor": (major, minor + 1, 0), "patch": (major, minor, patch + 1)}[kind]
    new_version = "%d.%d.%d" % new
    updated = pattern.sub(lambda m: m.group(1) + new_version + m.group(3), text, count=1)
    tmp = path + ".tmp"
    try:
        with open(tmp, "wb") as f:
            f.write(updated.encode("utf-8"))
        shutil.copymode(path, tmp)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    return new_version


def bump_from_labels(labels_json, pr=None):
    """マージされた PR のラベル（名前の JSON 配列）から、版の上げ方を 1 つ決める。

    ちょうど 1 つの major / minor / patch（大文字小文字は問わない）があるときだけ決める。無いとき・複数あるとき・
    ラベルの一覧が読めないときは、patch などに決めず PackageError で止める（何も push・公開しない）。
    複数あるときに大きい方を選ばないのは、誤って付けた major で版が大きく上がっても公開した版は取り戻せないため
    （止まっても workflow_dispatch で上げ方を指定して出せる）。ほかのラベルは無視する。
    メッセージは 1 行（GitHub の注記にそのまま入れる）。"""
    where = "PR #%s" % pr if pr else "この PR"
    stop = "版を決めずに止めました。何も push・公開していません。"
    try:
        labels = json.loads(labels_json)
    except (TypeError, ValueError):
        labels = None
    if not isinstance(labels, list) or not all(isinstance(name, str) for name in labels):
        raise PackageError("%s のラベルの一覧を読めません（名前の JSON 配列ではありません）。%s" % (where, stop))
    found = sorted({name.lower() for name in labels} & set(BUMP_KINDS))
    if not found:
        raise PackageError(
            "%s に major / minor / patch のラベルが無いので、%sActions の Release を workflow_dispatch で bump を指定して実行してください"
            "（この実行の再実行ではラベルが反映されません）。" % (where, stop))
    if len(found) > 1:
        raise PackageError(
            "%s のラベル（%s）から 1 つに決められないので、%sActions の Release を workflow_dispatch で bump を指定して実行してください。"
            % (where, ", ".join(found), stop))
    return found[0]


def build_zip(root, included, zip_path):
    """名前順・固定の時刻と権限で zip を書き、一時ファイルから最後に 1 回で置き換える。"""
    tmp = zip_path + ".tmp"
    try:
        with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED, compresslevel=COMPRESS_LEVEL) as z:
            for p in included:
                with open(os.path.join(root, *p.split("/")), "rb") as f:
                    data = f.read()
                info = zipfile.ZipInfo(p, date_time=ZIP_DATE)
                info.create_system = 3
                info.external_attr = ZIP_MODE
                info.compress_type = zipfile.ZIP_DEFLATED
                z.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=COMPRESS_LEVEL)
        os.replace(tmp, zip_path)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)


def verify_zip(root, included, zip_path, version):
    """書いた zip を開き直し、一覧・中身の CRC・package.json の版が元と合うか確かめる。"""
    with zipfile.ZipFile(zip_path) as z:
        bad = z.testzip()
        if bad is not None:
            raise PackageError("zip の中が壊れている: " + bad)
        names = z.namelist()
        if names != included:
            raise PackageError("zip の一覧が入れる物と違う")
        for info in z.infolist():
            with open(os.path.join(root, *info.filename.split("/")), "rb") as f:
                if zlib.crc32(f.read()) & 0xFFFFFFFF != info.CRC:
                    raise PackageError("元のファイルと中身が違う: " + info.filename)
            if info.date_time != ZIP_DATE or info.is_dir():
                raise PackageError("時刻・種類が固定になっていない: " + info.filename)
        packaged = json.loads(z.read("package.json").decode("utf-8"))
        if packaged.get("version") != version:
            raise PackageError("zip の package.json の版が違う")


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def collect(root):
    """root の git が管理するファイルから、(入れる物, 入れない物) を決める。確認も済ませる。"""
    included, excluded = partition(tracked_files(root))
    check_files_exist(root, included)
    check_meta(included)
    if "package.json" not in included:
        raise PackageError("package.json が入れる物の中に無い")
    return included, excluded


def default_root():
    here = os.path.dirname(os.path.abspath(__file__))
    out = subprocess.run(["git", "-C", here, "rev-parse", "--show-toplevel"], check=True, stdout=subprocess.PIPE)
    return out.stdout.decode("utf-8").strip()


def main(argv=None):
    ap = argparse.ArgumentParser(description="VCC（VPM）で配る zip を作る")
    ap.add_argument("--root", help="パッケージの根（既定はこの台本があるリポジトリ）")
    ap.add_argument("--out", help="zip を置くフォルダ（--list のとき以外は必須。無ければ作る）")
    ap.add_argument("--list", action="store_true", help="zip に入る物の一覧だけを出して終わる")
    ap.add_argument("--bump", choices=BUMP_KINDS, help="package.json の version を上げ、新しい版だけを出して終わる")
    ap.add_argument("--bump-from-labels", metavar="JSON", help="PR のラベル（名前の JSON 配列）から版の上げ方を 1 つ決め、それだけを出して終わる。決められなければ 1 で止まる")
    ap.add_argument("--pr", help="--bump-from-labels のメッセージに入れる PR の番号")
    ap.add_argument("--github-output", action="store_true", help="GITHUB_OUTPUT に zip-path・zip-sha256 を書く")
    args = ap.parse_args(argv)
    if not args.list and not args.bump and args.bump_from_labels is None and not args.out:
        ap.error("--out が要ります（--list・--bump・--bump-from-labels は不要）")
    try:
        if args.bump_from_labels is not None:
            print(bump_from_labels(args.bump_from_labels, args.pr))
            return 0
        root = os.path.abspath(args.root) if args.root else default_root()
        if args.bump:
            print(bump_version(root, args.bump))
            return 0
        name, version = read_manifest(root)
        included, excluded = collect(root)
        if args.list:
            print("\n".join(included))
            return 0
        os.makedirs(args.out, exist_ok=True)
        zip_path = os.path.join(os.path.abspath(args.out), "%s.%s.zip" % (name, version))
        build_zip(root, included, zip_path)
        verify_zip(root, included, zip_path, version)
    except (PackageError, subprocess.CalledProcessError, OSError, ValueError) as e:
        print("エラー: %s" % e, file=sys.stderr)
        return 1

    digest = sha256_of(zip_path)
    total = sum(os.path.getsize(os.path.join(root, *p.split("/"))) for p in included)
    print("%s" % zip_path)
    print("入れた: %d ファイル（元 %d バイト）→ zip %d バイト" % (len(included), total, os.path.getsize(zip_path)))
    print("SHA-256: %s" % digest)
    groups = Counter()
    for p, reason in excluded:
        groups[(p.split("/")[0], reason)] += 1
    print("入れなかった（git の管理下）: %d ファイル" % len(excluded))
    for (top, reason), count in sorted(groups.items()):
        print("  %-24s %4d 件  %s" % (top, count, reason))
    if args.github_output and os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as f:
            f.write("zip-path=%s\nzip-sha256=%s\n" % (zip_path, digest))
    return 0


if __name__ == "__main__":
    sys.exit(main())
