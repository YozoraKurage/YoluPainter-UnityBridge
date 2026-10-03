#!/usr/bin/env bash
# Unity の生死を変えずに、GPU メモリの値だけを日付別に記録する。
set -euo pipefail
export PYTHONDONTWRITEBYTECODE=1
exec python3 "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/gpu_memory_daemon.py" "$@"
