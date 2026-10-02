#!/usr/bin/env bash
# Dockerfile のビルド用ステージで、WSL2 の GPU 向けに新しい Mesa（d3d12 Gallium ドライバ）を
# /opt/mesa-d3d12 へビルドする。
#
# Ubuntu 22.04 の Mesa 23.2 の d3d12 は OpenGL 4.2 までしか出さず、Unity は 4.3 未満だと
# CopyTexture と compute shader を無効にする。24.x の d3d12 は 4.6 を出す。
#
# 失敗してもイメージのビルドは止めない（このスクリプトは常に 0 で終わる）。失敗したら
# /opt/mesa-d3d12/BUILD_FAILED にログの末尾を残し、コンテナはシステムの Mesa で動く。
set -uo pipefail

MESA_VERSION="${MESA_VERSION:-24.2.8}"
PREFIX=/opt/mesa-d3d12
LOG=/tmp/mesa-build.log

build() {
  set -e
  apt-get -q update
  apt-get -q install -y --no-install-recommends \
    build-essential pkg-config ninja-build bison flex ca-certificates curl xz-utils git \
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
  # swrast は drisw ローダが読む入口（GALLIUM_DRIVER=d3d12 でその中から d3d12 が選ばれる）。
  # LLVM は使わない（llvmpipe はシステムの Mesa が持っている）。
  meson setup build \
    --prefix="$PREFIX" --libdir=lib --buildtype=release --wrap-mode=default \
    -Dgallium-drivers=swrast,d3d12 -Dvulkan-drivers= \
    -Dplatforms=x11 -Dglx=dri -Dglvnd=enabled -Dshared-glapi=enabled \
    -Degl=disabled -Dgbm=disabled -Dgles1=disabled -Dgles2=disabled \
    -Dllvm=disabled -Dmicrosoft-clc=disabled -Dxlib-lease=disabled \
    -Dgallium-va=disabled -Dgallium-vdpau=disabled -Dgallium-xa=disabled \
    -Dgallium-nine=false -Dgallium-opencl=disabled -Dgallium-rusticl=false \
    -Dvalgrind=disabled -Dlibunwind=disabled -Dzstd=enabled -Dvideo-codecs=
  ninja -C build
  ninja -C build install
  echo "$MESA_VERSION" > "$PREFIX/VERSION"
}

mkdir -p "$PREFIX"
if ( build ) >"$LOG" 2>&1; then
  echo "Mesa ${MESA_VERSION} を ${PREFIX} にビルドした"
else
  { echo "Mesa ${MESA_VERSION} のビルドに失敗した（コンテナはシステムの Mesa で動く）。ログの末尾:"; tail -80 "$LOG"; } > "$PREFIX/BUILD_FAILED"
  cat "$PREFIX/BUILD_FAILED"
fi
exit 0
