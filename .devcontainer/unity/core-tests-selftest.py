#!/usr/bin/env python3
"""core-tests.sh の自己試験。小さな仮の Core / NUnit 試験を作り、Unity を使わず確かめる。"""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().with_name("core-tests.sh")
CORE = "namespace Probe { public static class Value { public static int Read() => 42; } }"
TESTS = r'''
using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
namespace Probe {
    [SetUpFixture] public class Scope {
        public static bool Ready;
        [OneTimeSetUp] public void Start() { Ready = true; }
        [OneTimeTearDown] public void End() { Ready = false; }
    }
    public class Basic {
        bool ready; int each;
        [OneTimeSetUp] public void Start() { Assert.That(Scope.Ready); ready = true; }
        [SetUp] public void Before() { each = 1; }
        [TearDown] public void After() { Assert.That(each, Is.EqualTo(2)); }
        [OneTimeTearDown] public void End() { Assert.That(ready); }
        [TestCase(1)] [TestCase(2)] public void Cases(int n) { Check(); Assert.That(n, Is.GreaterThan(0)); }
        public static IEnumerable Data { get { yield return new TestCaseData("日本語"); yield return new TestCaseData("<&"); } }
        [TestCaseSource(nameof(Data))] public void Sources(string value) { Check(); Assert.That(value, Is.Not.Empty); }
        [Test] public void Plain() { Check(); }
        [Test] public void Marker() { Check(); File.WriteAllText("ran.txt", "実行済み"); }
        void Check() { Assert.That(ready); Assert.That(each, Is.EqualTo(1)); each++; Assert.That(Value.Read(), Is.EqualTo(42)); }
    }
    [TestFixture(3)] [TestFixture(4)] public class Parameterized {
        readonly int n; public Parameterized(int n) { this.n = n; }
        [TestCase(1)] [TestCase(2)] public void Cases(int m) { Assert.That(n + m, Is.GreaterThan(3)); }
    }
    public class Optional {
        [Test, Ignore("属性で除外")] public void Ignored() { Assert.Fail(); }
        [Test] public void MissingBurst() { Assert.Ignore("Burst が無い"); }
        [Test, Explicit("明示したときだけ実行")] public void ExplicitFailure() { Assert.Fail("明示実行された"); }
    }
}
'''


class RunnerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="yolupainter-core-selftest-")
        cls.root = Path(cls.temporary.name)
        cls.source = cls.root / "source with spaces"
        (cls.source / "Runtime/Core").mkdir(parents=True)
        (cls.source / "Tests/Editor").mkdir(parents=True)
        cls.core = cls.source / "Runtime/Core/Value.cs"
        cls.tests = cls.source / "Tests/Editor/Basic.cs"
        cls.core.write_text(CORE)
        cls.tests.write_text(TESTS)
        cls.environment = dict(os.environ, YOLUPAINTER_CORE_CACHE=str(cls.root / "cache"))
        cls.serial = 0

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def tearDown(self):
        self.core.write_text(CORE)
        self.tests.write_text(TESTS)
        for path in (self.source / "Tests/Editor").glob("Extra*.cs"):
            path.unlink()

    def run_cli(self, *args, code=0, env=None):
        type(self).serial += 1
        out = self.root / f"result-{self.serial}"
        result = subprocess.run([str(SCRIPT), "--source", str(self.source), "--output", str(out), *args],
                                env=env or self.environment, text=True, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, timeout=90)
        self.assertEqual(result.returncode, code, result.stdout)
        return out, result.stdout

    def extra(self, name, text):
        path = self.source / "Tests/Editor" / ("Extra" + name + ".cs")
        path.write_text(text)
        return path

    def test_nunit_attributes_setup_sources_and_skips(self):
        out, _ = self.run_cli()
        result = ET.parse(out / "result.xml").getroot()
        self.assertEqual({k: result.get(k) for k in ("total", "passed", "failed", "skipped")},
                         dict(total="13", passed="10", failed="0", skipped="3"))
        self.assertTrue((out / "ran.txt").is_file())
        self.assertEqual(len([l for l in (out / "execution.tsv").read_text().splitlines() if l.startswith("終了")]), 13)

    def test_regex_selects_parameterized_fixtures_and_cases(self):
        out, _ = self.run_cli("--filter", r"^Probe[.]Parameterized\(3\)[.]Cases\(2\)$")
        result = ET.parse(out / "result.xml").getroot()
        self.assertEqual(result.get("total"), "1")
        self.assertEqual(result.get("passed"), "1")

    def test_assertion_failure_is_one_with_message(self):
        self.extra("Failure", 'using NUnit.Framework; namespace Probe { public class Failure { [Test] public void Fails() { Assert.Fail("意図した失敗 <&"); } } }')
        out, text = self.run_cli("--filter", "Probe[.]Failure[.]", code=1)
        self.assertIn("意図した失敗 <&", text)
        self.assertEqual(ET.parse(out / "result.xml").getroot().get("failed"), "1")

    def test_setup_failure_is_one(self):
        self.extra("Failure", 'using NUnit.Framework; namespace Probe { public class Failure { [OneTimeSetUp] public void Setup() { Assert.Fail("準備で失敗"); } [Test] public void Body() {} } }')
        out, text = self.run_cli("--filter", "Probe[.]Failure[.]", code=1)
        self.assertIn("準備で失敗", text)
        self.assertEqual(ET.parse(out / "result.xml").getroot().get("failed"), "1")

    def test_teardown_failure_is_one_even_when_every_case_passed(self):
        self.extra("Ending", 'using NUnit.Framework; namespace Probe { public class Ending { [OneTimeTearDown] public void End() { Assert.Fail("後始末で失敗"); } [Test] public void Body() {} } }')
        out, text = self.run_cli("--filter", "Ending[.]Body$", code=1)
        result = ET.parse(out / "result.xml").getroot()
        self.assertEqual(result.get("passed"), "1")
        self.assertEqual(result.get("failed"), "0")
        self.assertEqual(result.get("result"), "Failed")
        self.assertEqual(result.get("suite-failures"), "1")
        self.assertIn("スイート失敗 1", text)
        self.assertIn("後始末で失敗", text)

    def test_explicit_runs_when_filtered(self):
        self.run_cli("--filter", "ExplicitFailure$", code=1)

    def test_noncompiling_file_and_dependent_file_are_reported(self):
        bad = self.extra("Unity", 'public class MissingEngine { public UnityEngine.Color color; }')
        dependent = self.extra("Dependent", 'using NUnit.Framework; public class Dependent { [Test] public void NeedsEngine() { new MissingEngine(); } }')
        out, text = self.run_cli("--list", "--filter", "Probe[.]Basic[.]")
        inventory = json.loads((out / "inventory.json").read_text())
        for path in (bad, dependent):
            self.assertIn(str(path.relative_to(self.source)), inventory["excluded"])
            self.assertIn(path.name, text)
        self.assertEqual(inventory["selected"], 6)
        self.assertFalse((out / "ran.txt").exists(), "--list は試験本体を実行しない")
        self.assertFalse((out / "result.xml").exists())

    def test_partial_fixture_does_not_lose_its_setup_silently(self):
        self.extra("PartialA", 'using NUnit.Framework; namespace Probe { public partial class Partial { [SetUp] public void Setup() { UnityEngine.Debug.Log("準備"); } } }')
        self.extra("PartialB", 'using NUnit.Framework; namespace Probe { public partial class Partial { [Test] public void Body() {} } }')
        out, _ = self.run_cli("--list")
        inventory = json.loads((out / "inventory.json").read_text())
        self.assertEqual(len(inventory["excluded"]), 2)
        self.assertNotIn("Probe.Partial", inventory["classes"])

    def test_empty_filter_result_and_invalid_regex_are_three(self):
        for pattern in ("^NoSuchTest$", "["):
            with self.subTest(pattern=pattern):
                out, _ = self.run_cli("--filter", pattern, code=3)
                self.assertFalse((out / "result.xml").exists())

    def test_core_compile_error_is_three(self):
        self.core.write_text("namespace Probe { this cannot compile }")
        out, text = self.run_cli(code=3)
        self.assertIn("CS", text)
        self.assertFalse((out / "result.xml").exists())

    def test_discovery_error_is_three_not_partial_success(self):
        self.extra("Invalid", 'using NUnit.Framework; namespace Probe { public class Invalid { [TestCase(1)] public void WrongParameters() {} } }')
        out, _ = self.run_cli(code=3)
        self.assertTrue((out / "discovery.xml").exists())
        self.assertFalse((out / "result.xml").exists())

    def test_unsupported_async_test_is_three_not_silently_lost(self):
        self.extra("Async", 'using System.Threading.Tasks; using NUnit.Framework; namespace Probe { public class AsyncCase { [Test] public async Task Body() { await Task.Delay(1); } } }')
        out, _ = self.run_cli(code=3)
        self.assertFalse((out / "result.xml").exists())

    def test_all_files_excluded_is_three_with_inventory(self):
        self.tests.write_text("public class Broken { public Missing value; }")
        out, _ = self.run_cli("--list", code=3)
        inventory = json.loads((out / "inventory.json").read_text())
        self.assertEqual(len(inventory["excluded"]), 1)
        self.assertEqual(inventory["discovered"], 0)

    def test_cache_reuses_build_but_reruns_tests_and_detects_same_size_edit(self):
        self.run_cli("--filter", "Probe[.]Basic[.]")
        out, _ = self.run_cli("--filter", "Probe[.]Basic[.]")
        self.assertTrue(json.loads((out / "wall-time.json").read_text())["cacheHit"])
        self.assertTrue((out / "ran.txt").exists())
        before = self.core.stat()
        self.core.write_text(CORE.replace("42", "43"))
        os.utime(self.core, ns=(before.st_atime_ns, before.st_mtime_ns))
        out, _ = self.run_cli("--filter", "Probe[.]Basic[.]", code=1)
        self.assertFalse(json.loads((out / "wall-time.json").read_text())["cacheHit"])

    def test_new_test_file_is_discovered_and_deletion_invalidates_cache(self):
        path = self.extra("New", 'using NUnit.Framework; namespace Probe { public class Added { [Test] public void NewCase() {} } }')
        out, _ = self.run_cli("--filter", "Added[.]NewCase$")
        self.assertEqual(ET.parse(out / "result.xml").getroot().get("passed"), "1")
        path.unlink()
        self.run_cli("--filter", "Added[.]NewCase$", code=3)

    def test_no_cache_rebuilds(self):
        out, _ = self.run_cli("--no-cache", "--filter", "Probe[.]Basic[.]")
        self.assertFalse(json.loads((out / "wall-time.json").read_text())["cacheHit"])

    def test_bad_arguments_and_missing_dependency_are_three(self):
        self.run_cli("--unknown-option", code=3)
        self.run_cli("--filter", code=3)
        self.run_cli(env=dict(self.environment, YOLUPAINTER_CORE_NUNIT=str(self.root / "absent.dll")), code=3)

    def test_existing_output_is_refused_without_reusing_results(self):
        out, _ = self.run_cli("--filter", "Probe[.]Basic[.]")
        saved = (out / "result.xml").read_bytes()
        result = subprocess.run([str(SCRIPT), "--source", str(self.source), "--output", str(out)],
                                env=self.environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=30)
        self.assertEqual(result.returncode, 3, result.stdout)
        self.assertEqual((out / "result.xml").read_bytes(), saved)

    def test_concurrent_cold_builds_publish_complete_cache_and_separate_results(self):
        self.extra("Concurrent", 'using NUnit.Framework; namespace Probe { public class Concurrent { [Test] public void Body() {} } }')
        processes = []
        for i in range(2):
            out = self.root / f"concurrent-{i}"
            log = open(self.root / f"concurrent-{i}.log", "w+")
            process = subprocess.Popen([str(SCRIPT), "--source", str(self.source), "--output", str(out),
                                        "--filter", "Concurrent[.]Body$"], env=self.environment, stdout=log, stderr=subprocess.STDOUT)
            processes.append((process, out, log))
        try:
            for process, out, log in processes:
                code = process.wait(timeout=90)
                log.seek(0)
                self.assertEqual(code, 0, log.read())
                self.assertEqual(ET.parse(out / "result.xml").getroot().get("passed"), "1")
            out, _ = self.run_cli("--filter", "Concurrent[.]Body$")
            self.assertTrue(json.loads((out / "wall-time.json").read_text())["cacheHit"])
        finally:
            for process, _, log in processes:
                if process.poll() is None:
                    process.kill()
                    process.wait()
                log.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
