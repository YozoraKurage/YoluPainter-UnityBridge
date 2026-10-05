#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""package.py の試験。標準ライブラリの unittest だけで動く（Unity は要らない）。

    python3 .github/scripts/test_package.py
"""
import contextlib
import io
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
import zipfile

sys.dont_write_bytecode = True  # リポジトリに __pycache__ を残さない
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import package  # noqa: E402


def quiet_main(args):
    """package.main を、標準出力・標準エラーを捨てて呼ぶ。"""
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        return package.main(args)


def capture_main(args):
    """package.main を呼び、(終了コード, 標準出力, 標準エラー) を返す。"""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = package.main(args)
    return code, out.getvalue(), err.getvalue()


def write(root, rel, data=b"x"):
    full = os.path.join(root, *rel.split("/"))
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "wb") as f:
        f.write(data if isinstance(data, bytes) else data.encode("utf-8"))


def git_add_all(root):
    subprocess.run(["git", "-C", root, "init", "-q"], check=True)
    subprocess.run(["git", "-C", root, "add", "-A", "-f"], check=True)


def make_repo(root, version="1.2.3", extra=()):
    """Unity のパッケージに見立てた小さなリポジトリを作る。"""
    write(root, "package.json", json.dumps({"name": "net.example.pkg", "version": version}))
    write(root, "package.json.meta")
    write(root, "README.md")
    write(root, "README.md.meta")
    write(root, "Editor.meta")
    write(root, "Editor/A.cs")
    write(root, "Editor/A.cs.meta")
    write(root, "Tests.meta")
    write(root, "Tests/T.cs")
    write(root, "Tests/T.cs.meta")
    write(root, "Tests/Fixtures~/f.bin", b"fixture")          # 入れない: ~ で終わる（INCLUDE_NESTED_TILDE の外）
    write(root, "Tests/Editor/Persistence/Fixtures~/format1.ylp", b"ylp")  # 入れる: 試験が読む実物（.meta は要らない）
    write(root, "Tests/Editor/Persistence/Fixtures~/inner~/x.ylp")         # 入れない: その下の入れ子の ~
    write(root, "BrushSets~/Set/brush.gbr", b"\x00brush")      # 入れる: 同梱の筆先
    write(root, "Documentation~/THIRD_PARTY.md", "notice")    # 入れる: 表記
    write(root, "Documentation~/inner~/note.txt")              # 入れない: 入れ子の ~
    write(root, ".github/workflows/x.yml")                     # 入れない: . で始まる
    write(root, ".devcontainer/Dockerfile")
    write(root, ".gitignore")
    write(root, "temp~/user-data.fbx", b"secret")              # git add -f で管理下にしても入れない
    for rel in extra:
        write(root, rel)
    git_add_all(root)


class ClassifyTests(unittest.TestCase):
    def test_rules(self):
        cases = {
            "package.json": True,
            "Editor/Foo/Bar.cs": True,
            "Plugins/LiveLink/libyolu_bridge.so": True,
            "BrushSets~/Krita4Default/brushes/a.gbr": True,
            "Documentation~/licenses/live-link-native.txt": True,
            ".github/workflows/release.yml": False,
            ".gitignore": False,
            ".devcontainer/devcontainer.json": False,
            "Editor/.DS_Store": False,
            "Tests/Editor/Persistence/Fixtures~/format1.ylp": True,
            "Tests/Editor/Persistence/Fixtures~/inner~/x.ylp": False,
            "Tests/Editor/Other~/x.ylp": False,
            "Tests/Fixtures~/f.bin": False,
            "Documentation~/sub~/x.md": False,
            "temp~/dev/STATUS.md": False,
            "Validation~/x.txt": False,
            "Editor/backup.cs~": False,
        }
        for path, expected in cases.items():
            self.assertEqual(package.classify(path)[0], expected, path)

    def test_unknown_top_level_stops(self):
        for path in ("scripts/build.sh", "NOTES.md", "Samples/a.cs"):
            with self.assertRaises(package.PackageError, msg=path):
                package.classify(path)


class BuildTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = os.path.join(self.tmp.name, "repo")
        os.makedirs(self.root)
        self.out = os.path.join(self.tmp.name, "out")

    def run_main(self, *args):
        return quiet_main(["--root", self.root] + list(args))

    def zip_path(self, name="net.example.pkg.1.2.3.zip"):
        return os.path.join(self.out, name)

    def test_contents_and_layout(self):
        make_repo(self.root)
        self.assertEqual(self.run_main("--out", self.out), 0)
        with zipfile.ZipFile(self.zip_path()) as z:
            names = z.namelist()
            self.assertEqual(names, sorted(names, key=lambda n: n.encode("utf-8")))
            self.assertIn("package.json", names)  # 根に置く
            self.assertEqual(json.loads(z.read("package.json"))["version"], "1.2.3")
            self.assertEqual(z.read("BrushSets~/Set/brush.gbr"), b"\x00brush")
            self.assertEqual(z.read("Documentation~/THIRD_PARTY.md"), b"notice")
            for n in names:
                self.assertFalse(n.endswith("/"), n)  # フォルダだけの項目は作らない
                self.assertFalse(any(s.startswith(".") for s in n.split("/")), n)
            self.assertNotIn("Tests/Fixtures~/f.bin", names)
            self.assertEqual(z.read("Tests/Editor/Persistence/Fixtures~/format1.ylp"), b"ylp")
            self.assertNotIn("Tests/Editor/Persistence/Fixtures~/inner~/x.ylp", names)
            self.assertNotIn("Documentation~/inner~/note.txt", names)
            self.assertFalse([n for n in names if n.startswith(("temp~", ".github", ".devcontainer"))])
            for info in z.infolist():
                self.assertEqual(info.date_time, package.ZIP_DATE)
                self.assertEqual(info.external_attr, package.ZIP_MODE)

    def test_same_bytes_for_same_content(self):
        make_repo(self.root)
        self.assertEqual(self.run_main("--out", self.out), 0)
        first = package.sha256_of(self.zip_path())
        # 時刻と作る順が変わっても、中身が同じなら同じバイト列
        later = time.time() + 3600
        for dirpath, _, files in os.walk(self.root):
            for f in files:
                os.utime(os.path.join(dirpath, f), (later, later))
        out2 = os.path.join(self.tmp.name, "out2")
        self.assertEqual(self.run_main("--out", out2), 0)
        second = package.sha256_of(os.path.join(out2, "net.example.pkg.1.2.3.zip"))
        self.assertEqual(first, second)

    def test_untracked_files_do_not_ship(self):
        make_repo(self.root)
        write(self.root, "Editor/Untracked.cs")  # git add していない
        self.assertEqual(self.run_main("--out", self.out), 0)
        with zipfile.ZipFile(self.zip_path()) as z:
            self.assertNotIn("Editor/Untracked.cs", z.namelist())

    def tree(self):
        """作業フォルダの中身（.git を除く）の相対パスと内容の一覧。"""
        found = {}
        for dirpath, dirs, files in os.walk(self.root):
            dirs[:] = [d for d in dirs if d != ".git"]
            for f in files:
                full = os.path.join(dirpath, f)
                with open(full, "rb") as fh:
                    found[os.path.relpath(full, self.root)] = fh.read()
        return found

    def test_list_prints_the_files_and_writes_nothing(self):
        make_repo(self.root)
        before, here = self.tree(), os.getcwd()
        code, out, _ = capture_main(["--root", self.root, "--list", "--out", self.out])
        self.assertEqual(code, 0)
        self.assertEqual(out.splitlines(), package.collect(self.root)[0])  # 入れる物の一覧そのもの
        self.assertIn("BrushSets~/Set/brush.gbr", out.splitlines())
        self.assertFalse(os.path.exists(self.out))  # --out を渡しても作らない
        self.assertEqual(self.tree(), before)       # 作業フォルダも変わらない
        self.assertEqual(os.getcwd(), here)

    def test_version_follows_package_json(self):
        make_repo(self.root, version="0.3.1")
        self.assertEqual(self.run_main("--out", self.out), 0)
        self.assertTrue(os.path.isfile(self.zip_path("net.example.pkg.0.3.1.zip")))

    def test_unclassified_top_level_stops_without_zip(self):
        make_repo(self.root, extra=["Notes/todo.md"])
        code, _, err = capture_main(["--root", self.root, "--out", self.out])
        self.assertEqual(code, 1)
        self.assertIn("入れるか決まっていない: Notes/todo.md", err)
        self.assertFalse(os.path.exists(self.out) and os.listdir(self.out))

    def test_file_without_meta_stops(self):
        # Editor.meta は揃っているので、欠けているのはこのファイルの .meta だけ
        make_repo(self.root, extra=["Editor/NoMeta.cs"])
        code, _, err = capture_main(["--root", self.root, "--out", self.out])
        self.assertEqual(code, 1)
        self.assertIn(".meta が無いファイル: Editor/NoMeta.cs", err)
        self.assertNotIn(".meta が無いフォルダ", err)
        self.assertFalse(os.path.exists(self.out) and os.listdir(self.out))

    def test_folder_without_meta_stops(self):
        # ファイルの .meta は揃っていて、欠けているのは Runtime フォルダの .meta（Runtime.meta）だけ
        make_repo(self.root, extra=["Runtime/X.cs", "Runtime/X.cs.meta"])
        code, _, err = capture_main(["--root", self.root, "--out", self.out])
        self.assertEqual(code, 1)
        self.assertIn(".meta が無いフォルダ: Runtime", err)
        self.assertNotIn(".meta が無いファイル", err)
        self.assertFalse(os.path.exists(self.out) and os.listdir(self.out))

    def test_orphan_meta_stops(self):
        make_repo(self.root, extra=["Editor/Gone.cs.meta"])
        code, _, err = capture_main(["--root", self.root, "--out", self.out])
        self.assertEqual(code, 1)
        self.assertIn("対応する物の無い .meta: Editor/Gone.cs.meta", err)

    def test_meta_is_not_required_inside_tilde_folders(self):
        # ~ で終わるフォルダの中は Unity が取り込まないので .meta は要らない（入れる物でも）
        make_repo(self.root)
        included, _ = package.collect(self.root)
        self.assertIn("Tests/Editor/Persistence/Fixtures~/format1.ylp", included)
        self.assertIn("BrushSets~/Set/brush.gbr", included)
        self.assertNotIn("Tests/Editor/Persistence/Fixtures~/format1.ylp.meta", included)

    def test_bad_version_stops(self):
        for bad in ("1.2", "1.2.3-beta", "01.2.3", "../1.2.3", "latest"):
            with tempfile.TemporaryDirectory() as t:
                root = os.path.join(t, "r")
                os.makedirs(root)
                make_repo(root, version=bad)
                self.assertEqual(quiet_main(["--root", root, "--out", os.path.join(t, "o")]), 1, bad)

    def test_deleted_tracked_file_stops(self):
        make_repo(self.root)
        os.remove(os.path.join(self.root, "Editor", "A.cs"))
        self.assertEqual(self.run_main("--out", self.out), 1)

    def test_symlink_stops(self):
        make_repo(self.root)
        os.remove(os.path.join(self.root, "README.md"))
        os.symlink("Editor/A.cs", os.path.join(self.root, "README.md"))
        subprocess.run(["git", "-C", self.root, "add", "-A", "-f"], check=True)
        self.assertEqual(self.run_main("--out", self.out), 1)


class BumpTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = self.tmp.name
        self.path = os.path.join(self.root, "package.json")
        self.original = (
            '{\n  "name": "net.example.pkg",\n  "version": "0.3.9",\n  "displayName": "Example",\n'
            '  "dependencies": {\n    "com.unity.burst": "1.8.7"\n  }\n}\n'
        )
        with open(self.path, "w", encoding="utf-8", newline="") as f:
            f.write(self.original)
        os.chmod(self.path, 0o755)

    def bump(self, kind):
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            code = package.main(["--root", self.root, "--bump", kind])
        return code, out.getvalue()

    def test_kinds(self):
        for kind, expected in (("patch", "0.3.10"), ("minor", "0.4.0"), ("major", "1.0.0")):
            with open(self.path, "w", encoding="utf-8", newline="") as f:
                f.write(self.original)
            code, out = self.bump(kind)
            self.assertEqual((code, out), (0, expected + "\n"), kind)
            with open(self.path, encoding="utf-8") as f:
                self.assertEqual(f.read(), self.original.replace('"0.3.9"', '"%s"' % expected), kind)

    def test_only_the_version_line_changes_and_mode_is_kept(self):
        self.assertEqual(self.bump("patch")[0], 0)
        self.assertEqual(os.stat(self.path).st_mode & 0o777, 0o755)
        self.assertFalse(os.path.exists(self.path + ".tmp"))

    def test_rejects_unknown_kind(self):
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            package.main(["--root", self.root, "--bump", "1.2.3"])
        with self.assertRaises(package.PackageError):
            package.bump_version(self.root, "1.2.3")
        with open(self.path, encoding="utf-8") as f:
            self.assertEqual(f.read(), self.original)

    def test_stops_when_version_line_is_ambiguous(self):
        twice = self.original.replace('"displayName": "Example",', '"displayName": "Example",\n  "version": "0.3.9",')
        with open(self.path, "w", encoding="utf-8", newline="") as f:
            f.write(twice)
        self.assertEqual(self.bump("patch")[0], 1)
        with open(self.path, encoding="utf-8") as f:
            self.assertEqual(f.read(), twice)


class BumpFromLabelsTests(unittest.TestCase):
    """マージされた PR のラベルから版の上げ方を決める判定（release.yml が package.py --bump-from-labels で呼ぶ）。
    ラベルが無いのに patch で上がる、複数のラベルを黙って 1 つに寄せる、壊れた入力を「ラベルが無い」と言う、という退行を止める。"""

    def decide(self, labels_json):
        return package.bump_from_labels(labels_json, 7)

    def assertStops(self, labels_json, *words):
        with self.assertRaises(package.PackageError) as cm:
            self.decide(labels_json)
        message = str(cm.exception)
        self.assertNotIn("\n", message)  # GitHub の注記にそのまま入れる 1 行
        self.assertIn("PR #7", message)
        self.assertIn("何も push・公開していません", message)
        for w in words:
            self.assertIn(w, message)
        return message

    def test_one_label_decides(self):
        for kind in ("major", "minor", "patch"):
            self.assertEqual(self.decide(json.dumps([kind])), kind)

    def test_other_labels_are_ignored(self):
        self.assertEqual(self.decide('["bug", "minor", "docs"]'), "minor")
        self.assertEqual(self.decide('["majority", "patch-notes", "patch"]'), "patch")  # 名前が全部一致したものだけ

    def test_case_does_not_matter(self):
        self.assertEqual(self.decide('["Major"]'), "major")
        self.assertEqual(self.decide('["PATCH"]'), "patch")

    def test_no_label_stops_instead_of_defaulting_to_patch(self):
        for labels in ("[]", '["bug", "docs"]', '["majority"]', '[" patch"]'):
            self.assertStops(labels, "ラベルが無い", "workflow_dispatch")

    def test_several_labels_stop_instead_of_picking_one(self):
        message = self.assertStops('["major", "patch"]', "1 つに決められない", "major, patch")
        self.assertNotIn("ラベルが無い", message)
        self.assertStops('["minor", "major", "patch"]', "major, minor, patch")
        self.assertStops('["Minor", "major"]', "major, minor")  # 大文字小文字違いも別のラベルとして数える

    def test_same_label_twice_is_one_label(self):
        self.assertEqual(self.decide('["patch", "Patch"]'), "patch")

    def test_unreadable_input_stops_and_is_not_called_no_label(self):
        for labels in ("", "null", "{}", '"patch"', "[1, 2]", '["patch", null]', "[", "major"):
            message = self.assertStops(labels, "読めません")
            self.assertNotIn("ラベルが無い", message, labels)

    def test_none_input_stops(self):
        with self.assertRaises(package.PackageError):
            package.bump_from_labels(None)

    def test_command_line(self):
        code, out, err = capture_main(["--bump-from-labels", '["bug", "minor"]', "--pr", "12"])
        self.assertEqual((code, out, err), (0, "minor\n", ""))  # 出すのは上げ方だけ（YAML がそのまま受ける）
        for labels in ("[]", '["major", "minor"]', "null", "oops"):
            code, out, err = capture_main(["--bump-from-labels", labels, "--pr", "12"])
            self.assertEqual((code, out), (1, ""), labels)  # 何も出さずに止まる
            self.assertTrue(err.startswith("エラー: PR #12 "), err)

    def test_decision_does_not_touch_package_json(self):
        with tempfile.TemporaryDirectory() as t:
            path = os.path.join(t, "package.json")
            with open(path, "w", encoding="utf-8") as f:
                f.write('{"name": "net.example.pkg", "version": "1.2.3"}')
            self.assertEqual(capture_main(["--root", t, "--bump-from-labels", '["major"]'])[0], 0)
            with open(path, encoding="utf-8") as f:
                self.assertIn('"1.2.3"', f.read())


class WorkflowTests(unittest.TestCase):
    """release.yml が、この台本の判定を使っていること（YAML の中のシェルに判定が戻ると、上の試験が何も守らなくなる）。"""

    @classmethod
    def setUpClass(cls):
        with open(os.path.join(package.default_root(), ".github", "workflows", "release.yml"), encoding="utf-8") as f:
            cls.text = f.read()

    def test_label_decision_is_delegated(self):
        self.assertIn("package.py --bump-from-labels", self.text)
        self.assertIn("package.py --bump ", self.text)
        self.assertIn("test_package.py", self.text)  # 配る前に試験を回す
        for inline in ("grep -x", "grep -q", 'type="patch"', "|| true"):
            self.assertNotIn(inline, self.text, "ラベルの判定を YAML のシェルに戻さない")

    def test_skipped_runs_do_not_share_the_release_group(self):
        # マージされずに閉じた PR の実行が、待っている本物のリリースを押し出さない
        self.assertIn("format('release-skipped-{0}', github.run_id)", self.text)
        top = self.text.split("\njobs:\n")[0]
        self.assertNotIn("concurrency:", top, "concurrency は skip される実行が入らないよう job の中に置く")


class RealRepositoryTests(unittest.TestCase):
    """今のリポジトリの分類が全部決まっていて、同梱の筆先と表記が入ること（VCC の zip から欠けた不具合の回帰）。"""

    @classmethod
    def setUpClass(cls):
        cls.root = package.default_root()
        cls.included, cls.excluded = package.collect(cls.root)

    def test_bundled_brushes_and_notices_ship(self):
        inc = set(self.included)
        self.assertIn("package.json", inc)
        self.assertIn("Documentation~/THIRD_PARTY.md", inc)
        self.assertIn("Documentation~/licenses/live-link-native.txt", inc)
        brushes = [p for p in inc if p.startswith("BrushSets~/Krita4Default/brushes/")]
        self.assertGreater(len(brushes), 10)
        # BundledBrushSets.cs が読む場所
        self.assertTrue(any(p.endswith(".gbr") or p.endswith(".png") for p in brushes))

    def test_files_the_package_reads_from_itself_ship(self):
        """コードが PackagePaths.Physical / Asset で読む物が zip に入っていること。
        Tests を入れて Fixtures~ を外すと、使う人が testables で試験を回したときに落ちる（今まで出ていた zip の不具合の回帰）。"""
        import re
        inc = set(self.included)
        reads = {}
        for folder in ("Editor", "Tests"):
            for dirpath, _, files in os.walk(os.path.join(self.root, folder)):
                for name in files:
                    if not name.endswith(".cs"):
                        continue
                    full = os.path.join(dirpath, name)
                    with open(full, encoding="utf-8") as f:
                        for m in re.finditer(r'PackagePaths\.(?:Physical|Asset)\(\s*"([^"]+)"', f.read()):
                            reads.setdefault(m.group(1), os.path.relpath(full, self.root))
        self.assertTrue(any("Fixtures~" in r for r in reads), "読む物の拾い方が壊れている")
        for rel, source in sorted(reads.items()):
            folder = rel if rel.endswith("/") else rel + "/"
            self.assertTrue(rel in inc or any(p.startswith(folder) for p in inc) or any(p.startswith(rel) for p in inc),
                            "zip に入っていない: %s（%s が読む）" % (rel, source))

    def test_nothing_private_ships(self):
        for p in self.included:
            top = p.split("/")[0]
            self.assertNotIn(top, (".github", ".devcontainer", ".git", "temp~", "Validation~"), p)
            self.assertFalse(any(s.startswith(".") for s in p.split("/")), p)
            self.assertFalse(p.endswith((".alf", ".ulf")), p)


if __name__ == "__main__":
    unittest.main(verbosity=2)
