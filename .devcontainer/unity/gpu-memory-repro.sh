#!/usr/bin/env bash
# 必要な道具: build-essential libglvnd-dev libx11-dev xvfb xauth、Python 3。
# /opt と常駐 Unity は変更せず、短命の GLX プロセスだけを起動する。
set -euo pipefail
export PYTHONDONTWRITEBYTECODE=1
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/common.sh"
scratch="$(mktemp -d /tmp/yolupainter-gpu-repro.XXXXXXXX)"
trap 'rm -rf "$scratch"' EXIT
cc -O2 -Wall -Wextra "$script_dir/gpu-memory-repro.c" -o "$scratch/repro" -lGL -lX11
exec_args=(python3 "$script_dir/gpu_memory_repro.py" --program "$scratch/repro")
"${exec_args[@]}" "$@"
