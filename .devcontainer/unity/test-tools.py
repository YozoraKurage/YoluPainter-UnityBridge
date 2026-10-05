#!/usr/bin/env python3
"""テスト道具の回帰・競合再現（Unity と実際の台は操作しない）。

  python3 .devcontainer/unity/test-tools.py [--scripts <確認する道具のフォルダ>]

道具のコピーと仮の受け口を指定の一時領域に置き、実際の flock・setsid・同期・親の集約を使う。
"""
import argparse
import fcntl
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import time
import unittest

SCRIPTS = Path(__file__).resolve().parent
SCRATCH = Path(os.environ.get('YOLUPAINTER_TOOL_TEST_TMP', tempfile.gettempdir()))

SERVER = r'''
import json, os, sys, time
from pathlib import Path
project = Path(sys.argv[sys.argv.index('-projectPath') + 1])
d = project / 'TestDaemon'
d.mkdir(parents=True, exist_ok=True)
(d / 'protocol').write_text('request-id-v1:' + str(os.getpid()))
locks = [os.readlink('/proc/self/fd/' + f) for f in os.listdir('/proc/self/fd') if f.isdigit() and os.path.exists('/proc/self/fd/' + f)]
(d / 'child-locks').write_text('\n'.join(p for p in locks if p.endswith('/client.lock')))
(d / 'launched').write_text(str(os.getpid()))
if '-quit' in sys.argv:
    sys.exit(0)
while (d / 'hold-start').exists():
    time.sleep(0.01)
while not (d / 'quit').exists():
    (d / 'alive').touch()
    request = d / 'request.json'
    if request.exists():
        try:
            value = json.loads(request.read_text())
            request.rename(d / 'running.json')
        except (OSError, ValueError):
            continue
        (d / 'seen.json').write_text(json.dumps(value))
        suffix = '-' + value['id'] if value.get('id') else ''
        if suffix:
            (d / ('status' + suffix)).write_text('Fixture.LongTest')
        while (d / 'hold-request').exists():
            time.sleep(0.01)
        if 'op' in value or 'exec' in value:
            (d / 'exec-result.txt').write_text('=> 仮の結果\n')
            code = 0
        else:
            plan = json.loads(os.environ.get('TOOL_TEST_PLAN', '{}'))
            mode = 'batch-gl' if '-batchmode' in sys.argv else 'gui'
            state = plan.get(mode + ':' + value.get('filter', ''), 'pass')
            code = {'pass': 0, 'fail': 1, 'missing': 0, 'compile': 3, 'dead': 5}[state]
            if state in ('pass', 'fail'):
                failed = int(state == 'fail')
                (d / ('result' + suffix + '.xml')).write_text('<test-run total="1" passed="%d" failed="%d" skipped="0" duration="0.1"></test-run>' % (1 - failed, failed))
        (d / 'running.json').unlink()
        (d / ('done' + suffix)).write_text(str(code) + '\n仮の受け口\n')
        if (d / 'overwrite-shared').exists():
            (d / 'done').write_text('1\n別の依頼\n')
            (d / 'result.xml').write_text('<test-run total="22" passed="0" failed="22"></test-run>')
    time.sleep(0.01)
'''


class Fixture:
    def __init__(self, plan=None, lifecycle=False):
        SCRATCH.mkdir(parents=True, exist_ok=True)
        self.root = Path(tempfile.mkdtemp(prefix='tools-', dir=SCRATCH))
        self.scripts = self.root / 'scripts'
        self.scripts.mkdir()
        for name in ('run-tests.sh', 'runners.sh', 'test-daemon.sh', 'daemon-lock.sh',
                     'daemon-request.py', 'unity-do.sh', 'exec-method.sh', 'sync-package.py', 'summarize-results.js', 'guard-gpu-bake.py'):
            shutil.copy2(SCRIPTS / name, self.scripts / name)
        self.source = self.root / 'source'
        self.source.mkdir()
        (self.source / 'package.json').write_text('{}')
        (self.source / 'README.md').write_text('仮のパッケージ\n')
        self.runners = self.root / 'runners'
        self.processes = []
        self.env = dict(os.environ, YOLUPAINTER_GPU='0', TOOL_TEST_PLAN=json.dumps(plan or {}), YOLUPAINTER_ALLOW_PLAIN_FULL='1')  # 担当の全件を断る決まりは道具の試験では外す
        for key in ('YOLUPAINTER_LOCK_HELD', 'YOLUPAINTER_DAEMON_SWITCHING', 'YOLUPAINTER_RUNNER',
                    'YOLUPAINTER_FULL_RUN', 'YOLUPAINTER_SOURCE', 'YOLUPAINTER_TEST_GROUP'):
            self.env.pop(key, None)
        self.write('runners.conf', '1 batch-gl\n2 gui\n4 gui\n')
        self.write('shard-filters.py', 'print("1\\tA")\nprint("1\\tB")\n')
        self.write('server.py', SERVER)
        self.write('unity-editor', '#!/bin/bash\nexec python3 "$(dirname "$0")/server.py" -batchmode "$@"\n', True)
        self.write('xvfb-run', '#!/bin/bash\nshift 3\nshift\nexec python3 "$(dirname "$0")/server.py" "$@"\n', True)
        self.write('sleep', '#!/usr/bin/env python3\nimport sys,time\ntime.sleep(min(float(sys.argv[1]), 0.03))\n', True)
        self.env['YOLUPAINTER_PROGRESS_INTERVAL'] = '0.1'
        self.env.pop('YOLUPAINTER_PROGRESS_OPEN', None)
        self.env['PATH'] = str(self.scripts) + ':' + self.env['PATH']
        self.write('common.sh', '''set -euo pipefail
readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly PACKAGE_ROOT="$(realpath "${SCRIPT_DIR}/../source")"
readonly RUNNERS_HOME="$(realpath "${SCRIPT_DIR}/../runners")"
readonly UNITY_RUNNER="${YOLUPAINTER_RUNNER:-0}"
runner_project() { echo "$RUNNERS_HOME/$1/project"; }
runner_package() { echo "$RUNNERS_HOME/$1/pkg"; }
readonly UNITY_PROJECT="$(runner_project "$UNITY_RUNNER")"
readonly UNITY_LOG_DIR="$UNITY_PROJECT/Logs"
readonly UNITY_EDITOR="$SCRIPT_DIR/unity-editor"
readonly UNITY_BIN="$SCRIPT_DIR/Unity"
runner_numbers() { echo '1 2 4'; }
runner_conf_mode() { if [[ "$1" == 1 ]]; then echo batch-gl; else echo gui; fi; }
runner_live_mode() {
  local pidf="$(runner_project "$1")/TestDaemon/daemon.pid"
  [[ -f "$pidf" ]] && kill -0 "$(cat "$pidf")" 2>/dev/null || { echo down; return; }
  local args; args="$(ps -o args= -p "$(cat "$pidf")")"
  if [[ "$args" == *-batchmode* ]]; then echo batch-gl; else echo gui; fi
}
restore_license() { :; }
have_license() { return 0; }
license_hint() { :; }
info() { echo "==> $*"; }
warn() { echo "警告: $*" >&2; }
die() { echo "エラー: $*" >&2; exit 1; }
''')
        (self.scripts / 'daemon').mkdir()
        self.write('daemon/YoluPainterTestDaemon.cs', '// 仮の受け口\n')
        for n in (0, 1, 2, 4):
            p = self.runners / str(n) / 'project'
            for sub in ('TestDaemon', 'Logs', 'Packages', 'ProjectSettings', 'Assets'):
                (p / sub).mkdir(parents=True)
            (p / 'Packages/manifest.json').write_text(json.dumps({'dependencies': {'test': 'file:' + str(self.source)}}))
        if not lifecycle:
            self.write('runners.sh', '#!/bin/bash\nexit 0\n', True)

    def write(self, name, content, executable=False):
        p = self.scripts / name
        p.write_text(content)
        if executable:
            p.chmod(0o755)

    def daemon(self, n):
        return self.runners / str(n) / 'project/TestDaemon'

    def start(self, args, env=None):
        p = subprocess.Popen(args, env=dict(self.env, **(env or {})), stdout=subprocess.PIPE,
                             stderr=subprocess.STDOUT, text=True, start_new_session=True)
        self.processes.append(p)
        return p

    def run(self, name, *args, env=None):
        p = self.start([str(self.scripts / name), *args], env)
        out, _ = p.communicate(timeout=20)
        return p.returncode, out

    def run_shell_restart(self):
        p = self.start(['bash', '-c', 'source "$1/common.sh"; readonly DAEMON_DIR="$RUNNERS_HOME/2/project/TestDaemon"; source "$1/daemon-lock.sh"; acquire_daemon_client_lock; YOLUPAINTER_LOCK_HELD=1 setsid -f "$1/runners.sh" restart 2 >"$DAEMON_DIR/restart.log" 2>&1 </dev/null', 'fixture', str(self.scripts)], {'YOLUPAINTER_RUNNER': '2'})
        out, _ = p.communicate(timeout=10)
        return p.returncode, out

    def server(self, n):
        args = ['python3', str(self.scripts / 'server.py'), '-projectPath', str(self.daemon(n).parent)]
        if n == 1:
            args.append('-batchmode')
        p = self.start(args)
        (self.daemon(n) / 'daemon.pid').write_text(str(p.pid))
        self.wait(self.daemon(n) / 'alive')

    def wait(self, path, predicate=None):
        end = time.monotonic() + 10
        while time.monotonic() < end:
            if path.exists() and (predicate is None or predicate(path)):
                return
            time.sleep(0.01)
        raise AssertionError('仮の台の待機が時間切れ: ' + str(path.relative_to(self.root)))

    def close(self):
        for p in self.root.rglob('hold-*'):
            p.unlink(missing_ok=True)
        for d in self.root.rglob('TestDaemon'):
            (d / 'quit').touch()
            for name in ('daemon.pid', 'launched'):
                try:
                    pid = int((d / name).read_text())
                    if pid != os.getpid():
                        os.kill(pid, signal.SIGTERM)
                except (OSError, ValueError):
                    pass
        for p in self.processes:
            if p.poll() is None:
                os.killpg(p.pid, signal.SIGTERM)
            try:
                p.communicate(timeout=3)
            except subprocess.TimeoutExpired:
                os.killpg(p.pid, signal.SIGKILL)
                p.communicate()
        shutil.rmtree(self.root)


class ToolTests(unittest.TestCase):
    def fixture(self, **kwargs):
        f = Fixture(**kwargs)
        self.addCleanup(f.close)
        return f

    def test_full_waits_past_short_limit_and_stale_heartbeat(self):
        for args in (['--release'], ['--full', '--filter', 'Fixture'], [],
                     ['--filter', 'Shard']):
            with self.subTest(args=args):
                f = self.fixture()
                f.server(1)
                d = f.daemon(1)
                (d / 'hold-request').touch()
                env = {'YOLUPAINTER_REQUEST_TIMEOUT': '0.2'}
                if args == ['--filter', 'Shard']:
                    env['YOLUPAINTER_FULL_RUN'] = '1'
                p = f.start([str(f.scripts / 'run-tests.sh'), '--runner', '1', *args], env)
                f.wait(d / 'seen.json')
                os.utime(d / 'alive', (time.time() - 600, time.time() - 600))  # 鼓動が 10 分古い（停止の判定 30 分の手前）
                time.sleep(0.5)
                self.assertIsNone(p.poll(), '全件を短い依頼の上限で打ち切った')
                (d / 'hold-request').unlink()
                out, _ = p.communicate(timeout=10)
                self.assertEqual(0, p.returncode, out)
                self.assertIn('Fixture.LongTest', out)

    def test_full_wait_stops_on_absolute_limit_and_stalled_heartbeat(self):
        for label, env, stale in (('絶対上限', {'YOLUPAINTER_FULL_TIMEOUT': '0.3'}, 0),
                                  ('鼓動の停止', {'YOLUPAINTER_STALL_TIMEOUT': '60'}, 3600)):
            with self.subTest(label):
                f = self.fixture()
                f.server(1)
                d = f.daemon(1)
                (d / 'hold-request').touch()
                p = f.start([str(f.scripts / 'run-tests.sh'), '--runner', '1', '--release'], env)
                f.wait(d / 'seen.json')
                if stale:
                    os.utime(d / 'alive', (time.time() - stale, time.time() - stale))
                out, _ = p.communicate(timeout=10)
                self.assertEqual(5, p.returncode, out)
                self.assertTrue((d / 'running.json').exists(), '依頼は台に残す')

    def test_previous_request_wait_is_bounded(self):
        for label, env, stale in (('絶対上限', {'YOLUPAINTER_FULL_TIMEOUT': '0.3'}, 0),
                                  ('鼓動の停止', {'YOLUPAINTER_STALL_TIMEOUT': '60'}, 3600)):
            with self.subTest(label):
                f = self.fixture()
                f.server(1)
                d = f.daemon(1)
                (d / 'hold-request').touch()
                (d / 'request.json').write_text(json.dumps({'op': 'snippet'}))
                f.wait(d / 'seen.json')
                if stale:
                    os.utime(d / 'alive', (time.time() - stale, time.time() - stale))
                p = f.start([str(f.scripts / 'run-tests.sh'), '--runner', '1', '--release'], env)
                out, _ = p.communicate(timeout=10)
                self.assertEqual(5, p.returncode, out)
                self.assertIn('前の依頼が残っている', out)
                self.assertFalse((d / 'request.json').exists(), '前の依頼の上に書かない')

    def test_unity_do_and_exec_method_wait_for_orphan_request(self):
        for name, args, env in (('unity-do.sh', ['--runner', '2', 'run', '-e', 'return 1;'], {}),
                                ('exec-method.sh', ['Fixture.Run'], {'YOLUPAINTER_RUNNER': '2'})):
            with self.subTest(name):
                f = self.fixture()
                f.server(2)
                d = f.daemon(2)
                (d / 'hold-request').touch()
                (d / 'request.json').write_text(json.dumps({'op': 'snippet'}))  # ロックを持たない残りの依頼
                f.wait(d / 'seen.json')
                p = f.start([str(f.scripts / name), *args], env)
                time.sleep(0.4)
                self.assertFalse((d / 'request.json').exists(), '残った依頼の終了前に書いた')
                (d / 'hold-request').unlink()
                out, _ = p.communicate(timeout=10)
                self.assertEqual(0, p.returncode, out)
                # 上限つき: 固まった残りには 5 で止まる
                f2 = self.fixture()
                f2.server(2)
                d2 = f2.daemon(2)
                (d2 / 'hold-request').touch()
                (d2 / 'request.json').write_text(json.dumps({'op': 'snippet'}))
                f2.wait(d2 / 'seen.json')
                p = f2.start([str(f2.scripts / name), *args], dict(env, YOLUPAINTER_FULL_TIMEOUT='0.3'))
                out, _ = p.communicate(timeout=10)
                self.assertEqual(5, p.returncode, out)
                self.assertFalse((d2 / 'request.json').exists())

    def test_cleanup_removes_old_orphans_but_not_current_ids(self):
        f = self.fixture()
        d = f.daemon(1)
        keep, gone = 'a' * 32, 'b' * 32
        (d / 'running.json').write_text(json.dumps({'id': keep}))
        old = time.time() - 8 * 86400
        names = ['result-%s.xml' % keep, 'status-%s' % keep, 'result-%s.xml' % gone, 'status-%s' % gone, 'request-%s.tmp' % gone]
        for n in names:
            (d / n).write_text('x')
            os.utime(d / n, (old, old))
        subprocess.run(['python3', str(f.scripts / 'daemon-request.py'), 'cleanup', str(d)], check=True)
        self.assertEqual([True, True, False, False, False], [(d / n).exists() for n in names])

    def test_orphan_previous_request_waits_before_source_sync(self):
        f = self.fixture()
        f.server(1)
        d = f.daemon(1)
        (d / 'hold-request').touch()
        (d / 'request.json').write_text(json.dumps({'op': 'snippet'}))
        f.wait(d / 'seen.json')
        f.write('sync-package.py', 'from pathlib import Path; Path(__file__).with_name("synced").touch()\n')
        p = f.start([str(f.scripts / 'run-tests.sh'), '--runner', '1', '--release'])
        time.sleep(0.5)
        self.assertFalse((f.scripts / 'synced').exists())
        self.assertFalse((d / 'request.json').exists())
        (d / 'hold-request').unlink()
        out, _ = p.communicate(timeout=10)
        self.assertEqual(0, p.returncode, out)
        self.assertIn('snippet', out)
        self.assertTrue((f.scripts / 'synced').exists())

    def test_results_survive_later_request_and_shared_overwrite(self):
        f = self.fixture(plan={'batch-gl:Later': 'fail'})
        f.server(1)
        d = f.daemon(1)
        (d / 'overwrite-shared').touch()
        rc, out = f.run('run-tests.sh', '--runner', '1', '--release')
        self.assertEqual(0, rc, out)
        self.assertIn('1 件 / 成功 1 / 失敗 0', out)
        first_id = json.loads((d / 'seen.json').read_text())['id']
        result = d / ('result-' + first_id + '.xml')
        before = result.read_bytes()
        rc, out = f.run('run-tests.sh', '--runner', '1', '--filter', 'Later')
        self.assertEqual(1, rc, out)
        self.assertEqual(before, result.read_bytes())
        self.assertEqual('0', (d / ('done-' + first_id)).read_text().splitlines()[0])
        self.assertEqual(2, len(list(d.glob('result-*.xml'))))

    def test_dead_daemon_returns_without_cold_run(self):
        f = self.fixture()
        f.server(1)
        d = f.daemon(1)
        (d / 'hold-request').touch()
        p = f.start([str(f.scripts / 'run-tests.sh'), '--runner', '1', '--release'])
        f.wait(d / 'seen.json')
        server = f.processes[0]
        server.terminate()
        server.wait(timeout=3)
        out, _ = p.communicate(timeout=10)
        self.assertEqual(5, p.returncode, out)
        self.assertTrue((d / 'running.json').exists())

    def test_old_protocol_is_rejected_before_sending(self):
        f = self.fixture()
        f.server(1)
        d = f.daemon(1)
        (d / 'protocol').unlink()
        rc, out = f.run('run-tests.sh', '--runner', '1', '--release')
        self.assertEqual(3, rc, out)
        self.assertFalse((d / 'seen.json').exists())

    def test_short_request_timeout_leaves_request_and_late_result(self):
        f = self.fixture()
        f.server(1)
        d = f.daemon(1)
        (d / 'hold-request').touch()
        rc, out = f.run('run-tests.sh', '--runner', '1', '--filter', 'Fixture',
                        env={'YOLUPAINTER_REQUEST_TIMEOUT': '0.2'})
        self.assertEqual(5, rc, out)
        request_id = json.loads((d / 'running.json').read_text())['id']
        (d / 'hold-request').unlink()
        f.wait(d / ('done-' + request_id))
        self.assertTrue((d / ('result-' + request_id + '.xml')).exists())

    def test_full_waits_while_own_request_is_queued(self):
        f = self.fixture()
        d = f.daemon(1)
        identity = 'a' * 32
        (d / 'daemon.pid').write_text(str(os.getpid()))
        (d / 'running.json').write_text(json.dumps({'op': 'snippet'}))
        (d / 'request.json').write_text(json.dumps({'id': identity}))
        p = f.start(['python3', str(f.scripts / 'daemon-request.py'), 'wait', str(d), identity, '1'],
                    {'YOLUPAINTER_REQUEST_TIMEOUT': '0.1'})
        time.sleep(0.4)
        self.assertIsNone(p.poll())
        (d / ('done-' + identity)).write_text('0')
        out, _ = p.communicate(timeout=10)
        (d / 'daemon.pid').unlink()  # 後始末で試験自身を止めない
        self.assertEqual(0, p.returncode, out)
        self.assertIn('snippet', out)

    def test_both_reports_progress_before_children_finish(self):
        f = self.fixture()
        for n in (1, 2):
            f.server(n)
            (f.daemon(n) / 'hold-request').touch()
        p = f.start([str(f.scripts / 'run-tests.sh'), '--both', '--release', '--shards', '1'])
        for n in (1, 2):
            f.wait(f.daemon(n) / 'seen.json')
        # 両方が保留中でも、親の要約出力を待たず進捗が届く。
        import select
        self.assertTrue(select.select([p.stdout], [], [], 2)[0])
        line = p.stdout.readline()
        self.assertIn('待機', line)
        for n in (1, 2):
            (f.daemon(n) / 'hold-request').unlink()
        out, _ = p.communicate(timeout=10)
        self.assertEqual(0, p.returncode, out)

    def test_completed_results_retention(self):
        f = self.fixture()
        d = f.daemon(1)
        for i in range(203):
            identity = '%032x' % i
            for name in ('done-' + identity, 'result-' + identity + '.xml'):
                p = d / name
                p.touch()
                os.utime(p, (100 + i, 100 + i))
        active = '%032x' % 0
        (d / 'running.json').write_text(json.dumps({'id': active}))
        recent = d / ('done-' + '%032x' % 203)
        recent.touch()
        rc, out = f.run('daemon-request.py', 'cleanup', str(d))
        self.assertEqual(0, rc, out)
        self.assertTrue((d / ('done-' + active)).exists())
        self.assertFalse((d / ('result-' + '%032x' % 1 + '.xml')).exists())
        self.assertTrue(recent.exists())
        self.assertEqual(201, len(list(d.glob('done-*'))))

    def test_plain_full_run_is_refused_for_agents(self):
        # 担当の絞り込みの無い全件は終了コード 6 で断り、--priority・--release・--full と絞り込みは断らない（2026-10-03 の決まり）
        f = self.fixture()
        f.server(2)
        env = {'YOLUPAINTER_ALLOW_PLAIN_FULL': ''}
        rc, out = f.run('run-tests.sh', '--mode', 'gui', '--source', str(f.source), env=env)
        self.assertEqual(6, rc, out)
        self.assertIn('担当の全件は回さない', out)
        for args in (['--filter', 'X'], ['--priority'], ['--release'], ['--full']):
            rc, out = f.run('run-tests.sh', '--mode', 'gui', '--source', str(f.source), *args, env=env)
            self.assertNotEqual(6, rc, (args, out))

    def test_shards_exit_codes_and_missing_summary(self):
        cases = [({}, 0), ({'gui:A': 'missing'}, 3), ({'gui:A': 'fail'}, 1),
                 ({'gui:A': 'missing', 'gui:B': 'fail'}, 1),
                 ({'gui:A': 'fail', 'gui:B': 'missing'}, 1), ({'gui:A': 'compile'}, 3)]
        for plan, expected in cases:
            with self.subTest(plan=plan):
                f = self.fixture(plan=plan)
                f.server(2)
                f.server(4)
                rc, out = f.run('run-tests.sh', '--shards', '2', '--mode', 'gui', '--gui-only', '--source', str(f.source))
                self.assertIn('合計（2 組）', out)
                if 'missing' in plan.values() or 'compile' in plan.values():
                    self.assertIn('結果の出なかった組: 1', out)
                self.assertEqual(expected, rc, out)

    def test_both_prefers_failure_and_propagates_missing(self):
        for plan, expected in [({'batch-gl:Fixture': 'compile', 'gui:Fixture': 'fail'}, 1),
                               ({'batch-gl:Fixture': 'fail', 'gui:Fixture': 'compile'}, 1),
                               ({'gui:Fixture': 'missing'}, 3), ({'gui:Fixture': 'compile'}, 3),
                               ({'gui:Fixture': 'dead'}, 5), ({}, 0)]:
            with self.subTest(plan=plan):
                f = self.fixture(plan=plan)
                f.server(1)
                f.server(2)
                rc, out = f.run('run-tests.sh', '--both', '--filter', 'Fixture', '--source', str(f.source))
                self.assertIn('──── GUI ────', out)
                self.assertEqual(expected, rc, out)

    def test_shard_parent_rejects_missing_summary_even_if_children_exit_zero(self):
        f = self.fixture()
        f.server(2)
        f.server(4)
        f.write('summarize-results.js', 'process.exit(0);\n')
        rc, out = f.run('run-tests.sh', '--shards', '2', '--mode', 'gui', '--gui-only', '--source', str(f.source))
        self.assertIn('結果の出なかった組: 2', out)
        self.assertEqual(3, rc, out)

    def test_both_auto_shards_propagates_missing(self):
        f = self.fixture(plan={'gui:A': 'missing'})
        for n in (1, 2, 4):
            f.server(n)
        rc, out = f.run('run-tests.sh', '--both', '--source', str(f.source))
        self.assertIn('結果の出なかった組: 1', out)
        self.assertEqual(3, rc, out)

    def test_sync_failure_is_no_result(self):
        f = self.fixture()
        f.server(2)
        f.write('sync-package.py', 'import sys\nsys.exit(42)\n')
        rc, out = f.run('run-tests.sh', '--runner', '2', '--source', str(f.source), '--filter', 'Fixture')
        self.assertEqual(3, rc, out)

    def test_summary_rejects_empty_and_truncated_results(self):
        f = self.fixture()
        for xml in ('', '<test-run total="1" passed="1" failed="0" duration="1">'):
            with self.subTest(xml=xml):
                path = f.root / 'result.xml'
                path.write_text(xml)
                p = f.start(['node', str(f.scripts / 'summarize-results.js'), str(path)])
                out, _ = p.communicate(timeout=10)
                self.assertEqual(3, p.returncode, out)

    def test_two_syncs_do_not_share_or_delete_temporary_files(self):
        f = self.fixture()
        other = f.root / 'other-source'
        shutil.copytree(f.source, other)
        (other / 'README.md').write_text('後の同期\n')
        dest = f.root / 'copy'
        gate = f.root / 'hold-copy'
        gate.touch()
        # copyfile の直後で片方だけを待たせ、もう片方を最後の掃除まで通す。
        wrapper = '''import runpy, shutil, sys, time
from pathlib import Path
original = shutil.copystat
script, source, dest, paused, gate = sys.argv[1:]
def pause(src, dst, **kwargs):
    if Path(src).name == 'README.md':
        Path(paused).touch()
        while Path(gate).exists(): time.sleep(0.01)
    return original(src, dst, **kwargs)
shutil.copystat = pause
sys.argv = [script, source, dest, '--checksum']
runpy.run_path(script, run_name='__main__')
'''
        first = f.start(['python3', '-c', wrapper, str(f.scripts / 'sync-package.py'), str(f.source), str(dest), str(f.root / 'copy-paused'), str(gate)])
        f.wait(f.root / 'copy-paused')
        rc, out = f.run('sync-package.py', str(other), str(dest), '--checksum')
        self.assertEqual(0, rc, out)
        gate.unlink()
        out, _ = first.communicate(timeout=10)
        self.assertEqual(0, first.returncode, out)
        self.assertEqual((f.source / 'README.md').read_bytes(), (dest / 'README.md').read_bytes())
        self.assertEqual({'README.md', 'package.json'}, {p.name for p in dest.iterdir()})

    def test_sync_cleans_temporary_file_on_copy_error(self):
        f = self.fixture()
        wrapper = '''import runpy, shutil, sys
def fail(*args, **kwargs): raise RuntimeError('仮のコピーエラー')
shutil.copystat = fail
script, source, dest = sys.argv[1:]
sys.argv = [script, source, dest]
runpy.run_path(script, run_name='__main__')
'''
        dest = f.root / 'copy'
        p = f.start(['python3', '-c', wrapper, str(f.scripts / 'sync-package.py'), str(f.source), str(dest)])
        p.communicate(timeout=10)
        self.assertNotEqual(0, p.returncode)
        self.assertEqual([], list(dest.iterdir()))

    def check_second_request_waits(self, name, args, env=None):
        f = self.fixture()
        f.server(2)
        d = f.daemon(2)
        (d / 'hold-request').touch()
        first = f.start([str(f.scripts / 'unity-do.sh'), '--runner', '2', '--source', str(f.source), 'run', '-e', 'return 1;'])
        f.wait(d / 'seen.json')
        second = f.start([str(f.scripts / name), *args], env)
        time.sleep(0.25)
        pending = (d / 'request.json').exists()
        (d / 'hold-request').unlink()
        self.assertFalse(pending, '先の依頼の終了前に、後の依頼が受け口を上書きした')
        for p in (first, second):
            out, _ = p.communicate(timeout=10)
            self.assertEqual(0, p.returncode, out)

    def test_exec_method_uses_request_lock(self):
        self.check_second_request_waits('exec-method.sh', ['Fixture.Run'], {'YOLUPAINTER_RUNNER': '2'})

    def test_stale_lock_flag_does_not_bypass_lock(self):
        self.check_second_request_waits('unity-do.sh', ['--runner', '2', 'run', '-e', 'return 2;'], {'YOLUPAINTER_LOCK_HELD': '1'})

    def test_launch_retains_parent_lock_and_child_drops_it(self):
        for n, mode in ((2, '--gui'), (1, '--batch-gl')):
            with self.subTest(mode=mode):
                f = self.fixture(lifecycle=True)
                d = f.daemon(n)
                (d / 'hold-start').touch()
                p = f.start(['bash', '-c', 'source "$1/common.sh"; readonly DAEMON_DIR="$UNITY_PROJECT/TestDaemon"; source "$1/daemon-lock.sh"; acquire_daemon_client_lock; export YOLUPAINTER_LOCK_HELD=1; exec "$1/test-daemon.sh" start "$2"', 'fixture', str(f.scripts), mode], {'YOLUPAINTER_RUNNER': str(n)})
                f.wait(d / 'launched')
                with (d / 'client.lock').open('w') as lock:
                    held = False
                    try:
                        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    except BlockingIOError:
                        held = True
                self.assertTrue(held, '常駐の準備が終わる前に、起動を待つ親がロックを手放した')
                self.assertEqual('', (d / 'child-locks').read_text(), '常駐する子がロックを引き継いだ')
                (d / 'hold-start').unlink()
                out, _ = p.communicate(timeout=10)
                self.assertEqual(0, p.returncode, out)
                with (d / 'client.lock').open('w') as lock:
                    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def test_full_run_restart_holds_lock_for_two_requests(self):
        f = self.fixture(lifecycle=True)
        f.server(2)
        d = f.daemon(2)
        old_pid = (d / 'launched').read_text()
        (d / 'hold-start').touch()
        rc, out = f.run('run-tests.sh', '--runner', '2', '--source', str(f.source), '--gui-only')
        self.assertEqual(0, rc, out)
        self.assertIn('裏で再起動する', out)
        f.wait(d / 'launched', lambda p: p.read_text() != old_pid)
        f.wait(d / 'daemon.pid', lambda p: p.read_text().strip() != old_pid)
        first = f.start([str(f.scripts / 'unity-do.sh'), '--runner', '2', 'run', '-e', 'return 1;'])
        second = f.start([str(f.scripts / 'run-tests.sh'), '--runner', '2', '--filter', 'Fixture', '--source', str(f.source)])
        time.sleep(0.25)
        pending = (d / 'request.json').exists() or (d / 'snippet-in.cs').exists()
        (d / 'hold-start').unlink()
        self.assertFalse(pending, '裏の再起動の準備中に後の依頼が受け口に入った')
        for p in (first, second):
            out, _ = p.communicate(timeout=10)
            self.assertEqual(0, p.returncode, out)
        self.assertEqual('', (d / 'child-locks').read_text())

    def test_background_restart_excludes_two_setup_syncs(self):
        f = self.fixture(lifecycle=True)
        d = f.daemon(2)
        # 仮の再起動は停止の窓で待つ。実台の start/stop は一切呼ばない。
        f.write('test-daemon.sh', '''#!/bin/bash
source "$(dirname "$0")/common.sh"
d="$UNITY_PROJECT/TestDaemon"
if [[ "$1" == stop ]]; then
  rm -f "$d/daemon.pid"
  touch "$d/restarting"
  while [[ -f "$d/hold-restart" ]]; do sleep 0.02; done
fi
''', True)
        f.write('sync-package.py', '''import os, time
from pathlib import Path
root = Path(__file__).parent.parent
slot = root / 'sync-active'
try:
    slot.mkdir()
except FileExistsError:
    (root / 'sync-overlap').touch()
(root / ('sync-enter-' + str(os.getpid()))).touch()
time.sleep(0.15)
try: slot.rmdir()
except FileNotFoundError: pass
''')
        subprocess.run(['git', 'init', '-q', str(f.source)], check=True, capture_output=True)
        subprocess.run(['git', '-C', str(f.source), 'add', '.'], check=True)
        subprocess.run(['git', '-C', str(f.source), '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', '仮の木'], check=True)
        (d / 'hold-restart').touch()
        rc, out = f.run_shell_restart()
        self.assertEqual(0, rc, out)
        f.wait(d / 'restarting')
        p1 = f.start([str(f.scripts / 'runners.sh'), 'setup', '2'])
        p2 = f.start([str(f.scripts / 'runners.sh'), 'setup', '2'])
        time.sleep(0.25)
        entered_early = bool(list(f.root.glob('sync-enter-*')))
        (d / 'hold-restart').unlink()
        self.assertFalse(entered_early, '裏の再起動のロックを無視して setup が同期を始めた')
        for p in (p1, p2):
            out, _ = p.communicate(timeout=10)
            self.assertEqual(0, p.returncode, out)
        self.assertFalse((f.root / 'sync-overlap').exists(), '同じ台に二つの同期が重なった')
        self.assertEqual(2, len(list(f.root.glob('sync-enter-*'))))

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--scripts', type=Path, default=SCRIPTS)
    options, remaining = parser.parse_known_args()
    SCRIPTS = options.scripts.resolve()
    unittest.main(argv=[__file__, *remaining], verbosity=2)
