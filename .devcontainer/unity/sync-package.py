#!/usr/bin/env python3
"""テストの台（runner）のパッケージの写しを、ソースのフォルダと同じ中身にする。

  sync-package.py <ソースのフォルダ> <写しのフォルダ> [--checksum]

- 変わったファイルだけを写す（中身の同じファイルは触らないので、Unity は変わった所だけを取り込み直す）。
  既定は大きさと更新時刻で比べる（作業ツリーから写すとき）。--checksum は中身で比べる（git archive で
  取り出した木のように、全部の更新時刻が同じになるとき）。
- 写したファイルは更新時刻もソースに合わせる。写しにだけあるファイル・フォルダは消す。
- 最上位の .git・temp~・.devcontainer・.github は写さない（temp~ にはユーザーのファイルとライセンスが
  置かれるので、テストの台へも複製しない。CLAUDE.md の決まり）。
- 最後に「写した / 消した / 同じ」の数を 1 行で出す。
"""
import filecmp
import os
import shutil
import sys

EXCLUDED_TOP = {".git", "temp~", ".devcontainer", ".github", ".worktrees", ".claude", ".agent"}  # .worktrees・.claude はエージェントの worktree の置き場、.agent は進捗メモ


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    checksum = "--checksum" in argv
    if len(args) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    source, dest = (os.path.abspath(a) for a in args)
    if not os.path.isfile(os.path.join(source, "package.json")):
        print("エラー: ソースにパッケージ（package.json）が無い: " + source, file=sys.stderr)
        return 2
    os.makedirs(dest, exist_ok=True)
    copied = removed = same = 0
    wanted = set()
    for root, dirs, files in os.walk(source):
        rel = os.path.relpath(root, source)
        if rel == ".":
            dirs[:] = [d for d in dirs if d not in EXCLUDED_TOP]
            files = [f for f in files if f not in EXCLUDED_TOP]
        dirs.sort()
        target_dir = dest if rel == "." else os.path.join(dest, rel)
        os.makedirs(target_dir, exist_ok=True)
        wanted.add(os.path.normpath(target_dir))
        for name in sorted(files):
            src = os.path.join(root, name)
            dst = os.path.join(target_dir, name)
            wanted.add(os.path.normpath(dst))
            if os.path.islink(src):
                continue  # パッケージにシンボリックリンクは無い。あっても写さない
            if os.path.isfile(dst) and not os.path.islink(dst):
                s, d = os.stat(src), os.stat(dst)
                if s.st_size == d.st_size and (filecmp.cmp(src, dst, shallow=False) if checksum else int(s.st_mtime) == int(d.st_mtime)):
                    same += 1
                    continue
            tmp = dst + ".sync-tmp"
            shutil.copyfile(src, tmp)
            shutil.copystat(src, tmp)
            os.replace(tmp, dst)  # 途中の状態を Unity に見せない
            copied += 1
    # 写しにだけあるものを消す（深い所から）
    for root, dirs, files in os.walk(dest, topdown=False):
        rel = os.path.relpath(root, dest)
        if rel != "." and rel.split(os.sep)[0] in EXCLUDED_TOP:
            continue
        for name in files:
            path = os.path.normpath(os.path.join(root, name))
            if path not in wanted:
                os.remove(path)
                removed += 1
        for name in dirs:
            path = os.path.normpath(os.path.join(root, name))
            if rel == "." and name in EXCLUDED_TOP:
                continue
            if path not in wanted and os.path.isdir(path) and not os.listdir(path):
                os.rmdir(path)
    print("同期: 写した %d / 消した %d / 同じ %d" % (copied, removed, same))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
