#!/usr/bin/env python3
"""記録常駐の検証。仮の計測だけを使い、Unity は操作しない。"""
import datetime as dt
import json
from pathlib import Path
import os
import subprocess
import sys
import tempfile
import time
import unittest

from gpu_memory_daemon import Journal, birth, query, service

HERE = Path(__file__).resolve().parent


class MemoryDaemonTests(unittest.TestCase):
    def test_rotation_keeps_daily_files_and_leaves_other_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            today = dt.date.today()
            for days in range(1, 7):
                (root / (str(today - dt.timedelta(days=days)) + ".jsonl")).write_text("{}\n")
            (root / "service.lock").touch()
            Journal(root, 3).append({"event": "sample"})
            self.assertEqual(3, len(list(root.glob("*.jsonl"))))
            self.assertTrue((root / "service.lock").exists())
            record = json.loads((root / (str(today) + ".jsonl")).read_text())
            self.assertIn("time_local", record)

    def test_stale_pid_does_not_target_other_process(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            pid = os.getpid()
            (root / "pid.json").write_text(json.dumps({"pid": pid, "birth": birth(pid)}))
            self.assertIsNone(service(root))

    def test_query_failure_is_an_error_not_zero_memory(self):
        with tempfile.TemporaryDirectory() as temporary:
            script = Path(temporary) / "query.sh"
            script.write_text("#!/bin/sh\necho '未対応' >&2\nexit 2\n")
            script.chmod(0o755)
            result = query(script)
            self.assertEqual(2, result["exit_code"])
            self.assertNotIn("adapters", result)

    def test_singleton_records_and_stops_without_touching_runners(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            script = root / "query.sh"
            script.write_text('#!/bin/sh\nprintf \'{"adapters":[{"shared":{"resident_bytes":1234}}]}\\n\'\n')
            script.chmod(0o755)
            args = [str(HERE / "gpu-memory-daemon.sh"), "--log-dir", str(root / "logs"),
                    "--interval", "0.1", "--program", str(script), "--runners-home", str(root / "runners")]
            def call(command):
                return subprocess.run([args[0], command, *args[1:]], text=True, capture_output=True, timeout=8)
            try:
                first = call("start")
                self.assertEqual(0, first.returncode, first.stderr)
                pid = service(root / "logs")
                self.assertIsNotNone(pid)
                second = call("start")
                self.assertEqual(0, second.returncode, second.stderr)
                self.assertIn("すでに起動中", second.stdout)
                self.assertEqual(pid, service(root / "logs"))
                other = root / "別の worktree"
                other.mkdir()
                (other / "gpu_memory_daemon.py").write_bytes((HERE / "gpu_memory_daemon.py").read_bytes())
                status = subprocess.run([sys.executable, str(other / "gpu_memory_daemon.py"), "status", *args[1:]],
                                        text=True, capture_output=True, timeout=3)
                self.assertEqual(0, status.returncode, status.stderr)
                # 開始時刻が変わった PID は止めない（PID の再利用を模した値）。
                pid_path = root / "logs/pid.json"
                saved = pid_path.read_text()
                wrong = json.loads(saved); wrong["birth"] = "別の開始時刻"
                pid_path.write_text(json.dumps(wrong))
                try:
                    self.assertEqual(0, call("stop").returncode)
                    os.kill(pid, 0)
                finally:
                    pid_path.write_text(saved)
                deadline = time.monotonic() + 3
                records = []
                while time.monotonic() < deadline:
                    records = [json.loads(line) for p in (root / "logs").glob("*.jsonl") for line in p.read_text().splitlines()]
                    if sum(r["event"] == "sample" for r in records) >= 2: break
                    time.sleep(0.05)
                self.assertEqual(1, sum(r["event"] == "start" for r in records))
                self.assertGreaterEqual(sum(r["event"] == "sample" for r in records), 2)
                sample = next(r for r in records if r["event"] == "sample")
                self.assertEqual(1234, sample["adapter"]["adapters"][0]["shared"]["resident_bytes"])
                self.assertIn("host_memory_bytes", sample)
                self.assertIn("time_utc", sample)
            finally:
                stopped = call("stop")
                self.assertEqual(0, stopped.returncode, stopped.stderr)
                self.assertIsNone(service(root / "logs"))


if __name__ == "__main__":
    unittest.main()
