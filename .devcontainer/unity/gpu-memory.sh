#!/usr/bin/env bash
# WSL の GPU 全体の専用／共有使用量。Python の ctypes だけで動き、ビルド不要。
set -euo pipefail
export PYTHONDONTWRITEBYTECODE=1
export LD_LIBRARY_PATH="/usr/lib/wsl/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec python3 "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/gpu_memory.py" "$@"
