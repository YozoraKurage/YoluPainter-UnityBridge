#!/usr/bin/env python3
"""テスト依頼の待機・進捗表示・保存期限。実行中の依頼は消さない。"""
import json
import os
import re
from pathlib import Path
import sys
import time


def read_request(path):
    try:
        value = json.loads(path.read_text())
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def alive(directory):
    try:
        pid = int((directory / 'daemon.pid').read_text())
        if pid <= 0:
            return False
        os.kill(pid, 0)
        return True
    except (OSError, ValueError):
        return False


def describe(directory):
    running = read_request(directory / 'running.json')
    queued = read_request(directory / 'request.json')
    request = running or queued
    label = request.get('id', '旧形式')
    name = request.get('filter') or request.get('op') or request.get('exec') or '全件'
    if running.get('id'):
        try:
            name = (directory / ('status-' + running['id'])).read_text().strip()
        except OSError:
            pass
    return f"{'実行中' if running else '受け付け待ち'} {label}: {name}" if request else '同期・受け口の空きを待機'


def heartbeat_age(directory):
    try:
        return time.time() - (directory / 'alive').stat().st_mtime
    except OSError:
        return 0


def wait(directory, request_id='', full=False):
    start = time.monotonic()
    report = start
    missing_since = None
    # 絞り込みの依頼の上限。全件・前の依頼の待ちは絶対上限（FULL）と鼓動の停止（STALL）で止める。
    limit = float(os.environ.get('YOLUPAINTER_REQUEST_TIMEOUT', '2400'))
    full_limit = float(os.environ.get('YOLUPAINTER_FULL_TIMEOUT', '10800'))
    stall_limit = float(os.environ.get('YOLUPAINTER_STALL_TIMEOUT', '1800'))
    interval = float(os.environ.get('YOLUPAINTER_PROGRESS_INTERVAL', '30'))
    while True:
        if request_id and (directory / ('done-' + request_id)).exists():
            return 0
        if not alive(directory):
            print('デーモンのプロセスが終了した（結果無し）', file=sys.stderr)
            return 5
        pending = (directory / 'request.json').exists() or (directory / 'running.json').exists()
        if not request_id and not pending:
            return 0
        now = time.monotonic()
        if now - start > full_limit:
            print('待機の絶対上限を超えた（依頼は台に残す）' if request_id else
                  '前の依頼が残っている（待機の絶対上限を超えた）', file=sys.stderr)
            return 5
        if heartbeat_age(directory) > stall_limit:
            print('デーモンの鼓動が止まった（固まっている可能性）' if request_id else
                  '前の依頼が残っている（デーモンの鼓動が止まった）', file=sys.stderr)
            return 5
        if request_id:
            owned = any(read_request(directory / p).get('id') == request_id
                        for p in ('request.json', 'running.json'))
            missing_since = None if owned else (missing_since or now)
            # running の削除から done の公開までの短い窓を許容する。
            if missing_since is not None and now - missing_since > 5:
                print('自分の依頼が受け口から消えた（結果無し）', file=sys.stderr)
                return 3
            if not full and now - start > limit:
                print('絞り込んだ依頼の待機上限を超えた（依頼は台に残す）', file=sys.stderr)
                return 5
        if now >= report:
            print(f'待機 {int(now - start)} 秒 / 自分の依頼 {request_id or "送信前"} / {describe(directory)}',
                  file=sys.stderr, flush=True)
            report = now + interval
        time.sleep(0.1)


def cleanup(directory):
    # 新しい200件と7日以内の完了結果は両方とも保護する。
    completed = sorted((p for p in directory.glob('done-*')
                        if re.fullmatch(r'done-[a-f0-9]{32}', p.name)),
                       key=lambda p: p.stat().st_mtime, reverse=True)
    cutoff = time.time() - 7 * 86400
    protected = {read_request(directory / p).get('id') for p in ('request.json', 'running.json')}
    for stale in list(directory.glob('result-*')) + list(directory.glob('status-*')) + list(directory.glob('*.tmp')):
        match = re.search(r'([a-f0-9]{32})', stale.name)
        try:
            if stale.stat().st_mtime < cutoff and not (match and match.group(1) in protected):
                stale.unlink()
        except OSError:
            pass
    for done in completed[200:]:
        if done.stat().st_mtime >= cutoff:
            continue
        request_id = done.name[5:]
        if any(read_request(directory / p).get('id') == request_id
               for p in ('request.json', 'running.json')):
            continue
        for name in (f'result-{request_id}.xml', f'durations-{request_id}.tsv', f'status-{request_id}', done.name):
            (directory / name).unlink(missing_ok=True)


if __name__ == '__main__':
    action, folder, *args = sys.argv[1:]
    directory = Path(folder)
    if action == 'cleanup':
        cleanup(directory)
    elif action == 'status':
        print(describe(directory), file=sys.stderr)
    else:
        sys.exit(wait(directory, args[0] if args else '', len(args) > 1 and args[1] == '1'))
