#!/usr/bin/env python3
"""GPU メモリの記録常駐。Unity の起動・停止・同期は行わない。"""
import argparse
import datetime as dt
import fcntl
import json
import os
from pathlib import Path
import re
import select
import signal
import subprocess
import sys
import threading
import time

HERE = Path(__file__).resolve().parent


def now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def birth(pid):
    """PID が再利用されても、記録常駐以外のプロセスを止めない。"""
    try:
        return Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19]
    except (OSError, IndexError):
        return None


def service(log_dir):
    try:
        value = json.loads((log_dir / "pid.json").read_text())
        pid = value["pid"]
        cmd = [os.fsdecode(part) for part in Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0") if part]
        own_script = any(Path(part).name == "gpu_memory_daemon.py" for part in cmd)
        log_argument = cmd.index("--log-dir") + 1 if "--log-dir" in cmd else len(cmd)
        same_log = log_argument < len(cmd) and Path(cmd[log_argument]).resolve() == log_dir.resolve()
        # 別の worktree の入口からでも同じ記録常駐を参照できる。
        if birth(pid) == value["birth"] and own_script and "run" in cmd and same_log:
            return pid
    except (OSError, ValueError, KeyError, TypeError):
        pass
    return None


class Journal:
    def __init__(self, directory, keep):
        self.directory, self.keep = directory, keep
        self.day = None

    def append(self, value):
        local = dt.datetime.now().astimezone()
        day = local.strftime("%Y-%m-%d")
        if day != self.day:
            self.day = day
            files = sorted(p for p in self.directory.iterdir()
                           if re.fullmatch(r"\d{4}-\d{2}-\d{2}\.jsonl", p.name) and p.is_file())
            # 今日の分を含めて最大 keep ファイル。それ以外のファイルには触らない。
            older = [p for p in files if p.name != day + ".jsonl"]
            for path in older[:max(0, len(older) - self.keep + 1)]:
                path.unlink()
        record = dict(value, time_local=local.isoformat())
        data = (json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n").encode()
        fd = os.open(self.directory / (day + ".jsonl"), os.O_CREAT | os.O_APPEND | os.O_WRONLY, 0o600)
        try:
            with os.fdopen(fd, "ab", closefd=False) as output:
                output.write(data)
                output.flush()
                os.fsync(fd)
        finally:
            os.close(fd)


def query(program, pid=0):
    command = [str(program), "--json"]
    if pid:
        command += ["--pid", str(pid)]
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=3)
        if result.returncode:
            return {"error": "gpu-memory の取得失敗", "exit_code": result.returncode,
                    "detail": result.stderr.strip()[-1000:]}
        return json.loads(result.stdout)
    except (OSError, ValueError, subprocess.TimeoutExpired) as ex:
        return {"error": type(ex).__name__}


def process_memory(program, pid):
    value = query(program, pid)
    if "adapters" not in value: return value
    # セグメント詳細・アダプター名は全体側にある。台ごとは確保量だけを小さく記録する。
    return {"time_utc": value.get("time_utc"), "adapters": [
        {key: adapter.get(key) for key in ("adapter_index", "luid", "scope", "pid", "dedicated", "shared")}
        for adapter in value["adapters"]]}


def runner_pids(root):
    found = {}
    candidates = [("0", Path.home() / "unity-testproject/TestDaemon/daemon.pid")]
    if root.is_dir():
        candidates += [(p.name, p / "project/TestDaemon/daemon.pid")
                       for p in root.iterdir() if p.name.isdigit()]
    for number, path in candidates:
        try:
            pid = int(path.read_text().strip())
            if pid > 0 and birth(pid) is not None:
                found[number] = pid
        except (OSError, ValueError):
            pass
    return found


def host_memory():
    wanted = {"MemAvailable", "MemTotal", "SwapTotal", "SwapFree"}
    try:
        return {key: int(value.split()[0]) * 1024
                for key, value in (line.split(":", 1) for line in Path("/proc/meminfo").read_text().splitlines())
                if key in wanted}
    except (OSError, ValueError):
        return {}


def run(args):
    args.log_dir.mkdir(parents=True, exist_ok=True)
    with (args.log_dir / "service.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            if args.ready_fd is not None:
                os.write(args.ready_fd, b"already\n"); os.close(args.ready_fd)
            return 0
        pid_path = args.log_dir / "pid.json"
        pid_path.write_text(json.dumps({"pid": os.getpid(), "birth": birth(os.getpid())}))
        stopped = threading.Event()
        for signum in (signal.SIGTERM, signal.SIGINT):
            signal.signal(signum, lambda *_: stopped.set())
        journal = Journal(args.log_dir, args.keep)
        journal.append({"event": "start", "time_utc": now(), "interval_seconds": args.interval,
                        "keep_files": args.keep, "kernel": os.uname().release})
        if args.ready_fd is not None:
            os.write(args.ready_fd, b"started\n"); os.close(args.ready_fd)
        try:
            while not stopped.is_set():
                started = time.monotonic()
                record = {"event": "sample", "time_utc": now(), "host_memory_bytes": host_memory(),
                          "adapter": query(args.program), "runners": {}}
                for number, pid in runner_pids(args.runners_home).items():
                    if stopped.is_set(): break
                    record["runners"][number] = {"pid": pid, "memory": process_memory(args.program, pid)}
                record["sample_seconds"] = round(time.monotonic() - started, 3)
                journal.append(record)
                stopped.wait(max(0.1, args.interval - (time.monotonic() - started)))
        finally:
            journal.append({"event": "stop", "time_utc": now()})
            pid_path.unlink(missing_ok=True)
    return 0


def start(args):
    args.log_dir.mkdir(parents=True, exist_ok=True)
    errors_path = args.log_dir / "service.log"
    if errors_path.exists() and errors_path.stat().st_size > 1048576:
        errors_path.replace(args.log_dir / "service.prev.log")
    read_fd, write_fd = os.pipe()
    command = [sys.executable, str(Path(__file__).resolve()), "run", "--log-dir", str(args.log_dir),
               "--interval", str(args.interval), "--keep", str(args.keep), "--program", str(args.program),
               "--runners-home", str(args.runners_home), "--ready-fd", str(write_fd)]
    try:
        with errors_path.open("ab") as errors:
            child = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=errors, stderr=errors,
                                     start_new_session=True, pass_fds=(write_fd,))
        os.close(write_fd); write_fd = None
        if not select.select([read_fd], [], [], 5)[0]:
            raise RuntimeError("記録常駐の起動通知が時間切れ（service.log を確認）")
        result = os.read(read_fd, 80).decode().strip()
        if result not in ("started", "already"):
            child.wait(timeout=1)
            raise RuntimeError("記録常駐を起動できなかった（service.log を確認）")
        print("GPU メモリの記録: " + ("起動した" if result == "started" else "すでに起動中") + " / " + str(args.log_dir))
        return 0
    finally:
        os.close(read_fd)
        if write_fd is not None: os.close(write_fd)


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description="GPU メモリだけを常時記録（台の起動・停止はしない）")
    parser.add_argument("command", choices=("start", "stop", "status", "run"), nargs="?", default="status")
    parser.add_argument("--log-dir", type=Path, default=Path(os.environ.get("YOLUPAINTER_GPU_MEMORY_LOG_DIR", str(Path.home() / ".cache/yolupainter-tests/gpu-memory"))))
    parser.add_argument("--interval", type=float, default=float(os.environ.get("YOLUPAINTER_GPU_MEMORY_INTERVAL", "5")))
    parser.add_argument("--keep", type=int, default=int(os.environ.get("YOLUPAINTER_GPU_MEMORY_KEEP", "14")))
    parser.add_argument("--program", type=Path, default=HERE / "gpu-memory.sh", help=argparse.SUPPRESS)
    parser.add_argument("--runners-home", type=Path, default=Path(os.environ.get("YOLUPAINTER_RUNNERS_HOME", str(Path.home() / "unity-runners"))))
    parser.add_argument("--ready-fd", type=int, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if not 0.1 <= args.interval <= 3600 or not 1 <= args.keep <= 365:
        parser.error("間隔は 0.1〜3600 秒、保持数は 1〜365 ファイル")
    args.log_dir = args.log_dir.expanduser().resolve()
    try:
        if args.command == "start": return start(args)
        if args.command == "run": return run(args)
        pid = service(args.log_dir)
        if args.command == "status":
            print(f"GPU メモリの記録: {'起動中 (PID ' + str(pid) + ')' if pid else '停止中'} / {args.log_dir}")
            return 0 if pid else 1
        if pid:
            os.kill(pid, signal.SIGTERM)
            deadline = time.monotonic() + 6
            while service(args.log_dir) and time.monotonic() < deadline:
                time.sleep(0.05)
            if service(args.log_dir): raise RuntimeError("記録常駐の終了待ちが時間切れ")
        print("GPU メモリの記録を停止した（Unity は操作しない）")
        return 0
    except (OSError, RuntimeError) as ex:
        print("GPU メモリの記録: " + str(ex), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
