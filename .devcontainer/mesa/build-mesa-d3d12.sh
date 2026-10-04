#!/usr/bin/env bash
# Dockerfile のビルド用ステージで、WSL2 の GPU 向けに新しい Mesa（d3d12 Gallium ドライバ）を
# /opt/mesa-d3d12 へビルドする。
#
# Ubuntu 22.04 の Mesa 23.2 の d3d12 は OpenGL 4.2 までしか出さず、Unity は 4.3 未満だと
# CopyTexture と compute shader を無効にする。24.2 の d3d12 は 4.6 を出す（RTX 3070 で実測）。
#
# 失敗してもイメージのビルドは止めない（外側は常に 0 で終わる）。失敗したら
# /opt/mesa-d3d12/BUILD_FAILED にログの末尾を残し、コンテナはシステムの Mesa で動く。
#
#   build-mesa-d3d12.sh            ビルドして結果を判定する（Dockerfile から呼ぶ）
#   build-mesa-d3d12.sh --inner    実際のビルド手順（外側から別プロセスで呼ばれる）

MESA_VERSION="${MESA_VERSION:-24.2.8}"
PREFIX="${MESA_PREFIX:-/opt/mesa-d3d12}"  # MESA_PREFIX はこのスクリプト自体の試験用
PATCH_DIR="${MESA_PATCH_DIR:-$(cd "$(dirname "$0")" && pwd)}"
LOG=/tmp/mesa-build.log

if [[ "${1:-}" == --inner ]]; then
  # 別プロセスのトップレベルなので set -e がそのまま効く（if の条件の中で関数を呼ぶと
  # set -e が無視され、途中の失敗を見逃す。前の版はそれで「成功」と誤報した）。
  set -euxo pipefail
  apt-get -q update
  apt-get -q install -y --no-install-recommends \
    build-essential pkg-config ninja-build bison flex ca-certificates curl xz-utils git patch \
    python3 python3-pip \
    libdrm-dev libexpat1-dev zlib1g-dev libzstd-dev libelf-dev libglvnd-dev \
    libx11-dev libx11-xcb-dev libxext-dev libxfixes-dev libxshmfence-dev libxxf86vm-dev libxrandr-dev \
    libxcb1-dev libxcb-glx0-dev libxcb-dri2-0-dev libxcb-dri3-dev libxcb-present-dev \
    libxcb-shm0-dev libxcb-sync-dev libxcb-xfixes0-dev libxcb-randr0-dev
  # Mesa 24 は meson 1.1 以上が要る（22.04 の apt は 0.61）。
  pip3 install --no-cache-dir 'meson>=1.3,<1.6' 'mako>=1.1' packaging pyyaml

  cd /tmp
  curl -fsSL "https://archive.mesa3d.org/mesa-${MESA_VERSION}.tar.xz" -o mesa.tar.xz
  tar xf mesa.tar.xz
  cd "mesa-${MESA_VERSION}"
  # D3D12_BUFFER_CACHE_MB でキャッシュだけを制限する。既定の寿命・上限は上流のまま。
  patch -p1 < "$PATCH_DIR/d3d12-buffer-cache.patch"
  # デバイスの消失時に HRESULT と DRED を残す。/opt への反映はコンテナの作り直しから。
  patch -p1 < "$PATCH_DIR/d3d12-device-diagnostics.patch"
  # 24.2 では swrast が softpipe + llvmpipe を意味し、llvmpipe は LLVM を要求する。LLVM は
  # 入れない（llvmpipe はシステムの Mesa が持っている）ので softpipe を明示する。
  # libGLX_mesa は libgallium を直接リンクするので、DRI ドライバ（*_dri.so）は作られない。
  meson setup build \
    --prefix="$PREFIX" --libdir=lib --buildtype=release --wrap-mode=default \
    -Dgallium-drivers=softpipe,d3d12 -Dvulkan-drivers= \
    -Dplatforms=x11 -Dglx=dri -Dglvnd=enabled -Dshared-glapi=enabled \
    -Degl=disabled -Dgbm=disabled -Dgles1=disabled -Dgles2=disabled \
    -Dllvm=disabled -Dmicrosoft-clc=disabled -Dxlib-lease=disabled \
    -Dgallium-va=disabled -Dgallium-vdpau=disabled -Dgallium-xa=disabled \
    -Dgallium-nine=false -Dgallium-opencl=disabled -Dgallium-rusticl=false \
    -Dvalgrind=disabled -Dlibunwind=disabled -Dzstd=enabled -Dvideo-codecs=
  ninja -C build
  ninja -C build install
  # 実行時に要らないもの（DirectX-Headers のヘッダと静的ライブラリ、pkg-config）を落とす。
  rm -rf "$PREFIX/include" "$PREFIX/lib/pkgconfig"
  find "$PREFIX/lib" -name '*.a' -delete
  test -f "$PREFIX/lib/libGLX_mesa.so.0"
  echo "$MESA_VERSION" > "$PREFIX/VERSION"
  printf '%s\n' d3d12-buffer-cache d3d12-device-diagnostics > "$PREFIX/PATCHES"
  exit 0
fi

mkdir -p "$PREFIX"
if MESA_VERSION="$MESA_VERSION" bash "$0" --inner >"$LOG" 2>&1 && [[ -f "$PREFIX/lib/libGLX_mesa.so.0" ]]; then
  echo "Mesa ${MESA_VERSION} を ${PREFIX} にビルドした"
else
  rm -rf "${PREFIX:?}"/*
  { echo "Mesa ${MESA_VERSION} のビルドに失敗した（コンテナはシステムの Mesa で動く）。ログの末尾:"; tail -80 "$LOG"; } > "$PREFIX/BUILD_FAILED"
  cat "$PREFIX/BUILD_FAILED"
fi
exit 0
