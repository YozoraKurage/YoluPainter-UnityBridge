#!/usr/bin/env python3
"""再現子の各同期点で全体常駐量と PID 別確保量を記録する。"""
import argparse
import json
import os
import subprocess
import sys
import time

from gpu_memory import Monitor


def main():
    parser = argparse.ArgumentParser(description="短命の Mesa GLX プロセスで GPU 解放を測る（JSON Lines）")
    parser.add_argument("--program", required=True)
    parser.add_argument("--size", type=int, default=4096)
    parser.add_argument("--objects", type=int, default=4)
    parser.add_argument("--iterations", type=int, default=24)
    parser.add_argument("--no-map", action="store_true", help="PBO のマップによる完了待ちを省く")
    parser.add_argument("--no-finish-each", action="store_true", help="最終周以外の glFinish を省く")
    parser.add_argument("--mesa-prefix", help="比較する Mesa の接頭辞（/opt は変更しない）")
    args = parser.parse_args()
    if not 16 <= args.size <= 4096 or not 1 <= args.objects <= 8 or not 1 <= args.iterations <= 256:
        parser.error("size は 16〜4096、objects は 1〜8、iterations は 1〜256")
    env = os.environ.copy()
    if args.mesa_prefix:
        env["LD_LIBRARY_PATH"] = args.mesa_prefix + "/lib:" + env.get("LD_LIBRARY_PATH", "")
    monitor = Monitor()
    child = None

    def emit(stage, iteration=0, pid=None):
        result = {"stage": stage, "iteration": iteration, "global": monitor.sample()}
        if pid:
            result["process"] = monitor.sample(pid=pid)
        print(json.dumps(result, ensure_ascii=False), flush=True)

    try:
        emit("before_process")
        command = ["xvfb-run", "-a", "-s", "-screen 0 64x64x24", args.program,
                   str(args.size), str(args.objects), str(args.iterations),
                   str(int(not args.no_map)), str(int(not args.no_finish_each))]
        child = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, env=env)
        for line in child.stdout:
            if not line.startswith("{"):
                print(line, end="", file=sys.stderr)
                continue
            stage = json.loads(line)
            # コンテキスト破棄で dxg のプロセスも登録解除されるため、PID 照会は終える。
            emit(stage["stage"], stage["iteration"],
                 None if stage["stage"] == "context_destroyed" else stage["pid"])
            child.stdin.write("\n")
            child.stdin.flush()
        code = child.wait()
        emit("process_exited")
        time.sleep(2)
        emit("process_exited_2s")
        return code
    finally:
        if child and child.poll() is None:
            child.stdin.close()
            child.wait(timeout=15)
        monitor.close()


if __name__ == "__main__":
    raise SystemExit(main())
