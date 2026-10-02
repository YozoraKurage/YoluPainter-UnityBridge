#!/usr/bin/env bash
# コンテナから GPU が使えているかを確かめる。
#
#   gpu-check.sh          OpenGL のレンダラ（xvfb 上、Unity と同じ環境変数）を表示
#   gpu-check.sh --unity  常駐 Unity（test-daemon.sh）が実際に使っているデバイスも表示
#
# 「llvmpipe」と出たら CPU 描画（GPU が通っていない）。「D3D12 (...)」と出たら実 GPU。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

if [[ -e /dev/dxg ]]; then info "/dev/dxg: あり"; else warn "/dev/dxg: 無い（devcontainer.json の --device=/dev/dxg と、WSL2 + Docker Desktop が要る）"; fi
if [[ -d /usr/lib/wsl/lib ]]; then info "/usr/lib/wsl/lib: あり ($(ls /usr/lib/wsl/lib | tr '\n' ' '))"; else warn "/usr/lib/wsl/lib: 無い（/usr/lib/wsl のマウントが要る）"; fi
info "GALLIUM_DRIVER=${GALLIUM_DRIVER:-（未設定 = llvmpipe）}"
if [[ -f /opt/mesa-d3d12/BUILD_FAILED ]]; then warn "同梱 Mesa のビルドは失敗している（/opt/mesa-d3d12/BUILD_FAILED）。システムの Mesa を使う"
elif [[ -f /opt/mesa-d3d12/VERSION ]]; then info "同梱 Mesa: $(cat /opt/mesa-d3d12/VERSION)（LIBGL_DRIVERS_PATH=${LIBGL_DRIVERS_PATH:-未使用}）"
else info "同梱 Mesa: 無し（システムの Mesa を使う）"; fi

if command -v glxinfo >/dev/null 2>&1; then
  xvfb-run -a glxinfo -B 2>&1 | grep -E 'OpenGL (renderer|core profile version|version) string|Accelerated|Video memory|error' || true
else
  warn "glxinfo が無い（mesa-utils。イメージを再ビルドすれば入る）"
fi

if [[ "${1:-}" == --unity ]]; then
  "$SCRIPT_DIR/unity-do.sh" run -e 'return SystemInfo.graphicsDeviceType+" / "+SystemInfo.graphicsDeviceName+" / "+SystemInfo.graphicsDeviceVersion+" / VRAM "+SystemInfo.graphicsMemorySize+"MB";' 2>&1 | sed -n 's/^=> /Unity: /p'
fi
