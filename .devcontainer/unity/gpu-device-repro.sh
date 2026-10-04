#!/usr/bin/env bash
# 例: YOLUPAINTER_MESA_PREFIX=/tmp/mesa-diagnostics gpu-device-repro.sh [--remove]
# --remove は、この短命プロセスの論理デバイスだけを消失させる。
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/common.sh"
# Mesa の device factory は同じプロセス内でもデバイスを分離できる。
# RemoveDevice の試験だけ、公開 CreateDevice と Mesa が同じ論理デバイスを返す形にする。
if [[ "${1:-}" == --remove ]]; then
  export D3D12_DEBUG="${D3D12_DEBUG:+$D3D12_DEBUG,}singleton"
fi
scratch="$(mktemp -d /tmp/yolupainter-device-repro.XXXXXXXX)"
trap 'rm -rf "$scratch"' EXIT
cc -O2 -Wall -Wextra -Werror "$script_dir/gpu-device-repro.c" -o "$scratch/repro" -lGL -lX11 -ldl
xvfb-run -a -s '-screen 0 64x64x24' "$scratch/repro" "$@"
