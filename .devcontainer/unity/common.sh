#!/usr/bin/env bash
# Unity 関連スクリプトの共通定義。単体では実行しない。

set -euo pipefail

readonly UNITY_BIN="${UNITY_PATH:-/opt/unity}/Editor/Unity"
readonly UNITY_EDITOR="/usr/bin/unity-editor" # xvfb-run + -batchmode を被せたラッパー

# テストの台（runner）。0 は今までのテストプロジェクト（名前付きボリューム。パッケージは file:/workspace を
# 直接読む）。1 以上は $RUNNERS_HOME/<番号>/project で、パッケージは台の中の写し（pkg。依頼のたびに作業ツリー
# かコミットから同期する。run-tests.sh）を読むので、ほかの作業者の書きかけやテスト中の保存の影響を受けない。
# 台の番号は YOLUPAINTER_RUNNER（各スクリプトの --runner N）、台とモードの一覧は runners.conf、作るのは runners.sh。
# テストプロジェクトは /workspace（ホストの 9p バインド）に置かない。Library/ の I/O で桁違いに遅くなる。
UNITY_RUNNER="${YOLUPAINTER_RUNNER:-0}"
[[ "$UNITY_RUNNER" =~ ^[0-9]+$ ]] || { echo "エラー: YOLUPAINTER_RUNNER は台の番号（0 以上の整数）: $UNITY_RUNNER" >&2; exit 2; }
readonly UNITY_RUNNER
readonly RUNNERS_HOME="${YOLUPAINTER_RUNNERS_HOME:-$HOME/unity-runners}"
# 0 の置き場は YOLUPAINTER_UNITY_PROJECT（devcontainer が設定する）で変えられる。台 1 以上はそれに依らない。
runner_project() { if [[ "$1" == 0 ]]; then echo "${YOLUPAINTER_UNITY_PROJECT:-$HOME/unity-testproject}"; else echo "$RUNNERS_HOME/$1/project"; fi; }
runner_package() { echo "$RUNNERS_HOME/$1/pkg"; }
UNITY_PROJECT="$(runner_project "$UNITY_RUNNER")"
readonly UNITY_PROJECT

# 再ビルドを跨いで .ulf を残しておく置き場（ボリューム）
readonly LICENSE_STORE="$HOME/.unity-license"
readonly LICENSE_STORE_FILE="$LICENSE_STORE/Unity_lic.ulf"
# Unity が実際に読む場所
readonly LICENSE_SYSTEM_FILE="/usr/share/unity3d/config/Unity_lic.ulf"
# 認証情報でのアクティベーションはこちら側に .ulf を書く。ここはボリュームでは
# ないので、控えは必ず LICENSE_STORE_FILE に取る。
readonly LICENSE_USER_FILE="$HOME/.local/share/unity3d/Unity/Unity_lic.ulf"

readonly UNITY_LOG_DIR="$UNITY_PROJECT/Logs"

# このスクリプト群のあるディレクトリ
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR

# パッケージ本体（= このリポジトリ）
readonly PACKAGE_ROOT="/workspace"

# WSL2 の GPU（/dev/dxg）が見えていれば、Mesa の d3d12 ドライバで OpenGL を実 GPU に通す
# （Unity → OpenGL → Mesa d3d12 → D3D12 → Windows の GPU ドライバ）。Linux 版 Unity は D3D11 を
# 使えないので API は OpenGL のままだが、llvmpipe（CPU 描画）よりずっと実機に近い。
# /dev/dxg が無い環境で d3d12 を強制すると OpenGL ごと起動しなくなるので、あるときだけ有効にする。
# YOLUPAINTER_GPU=0 で llvmpipe に戻せる。
if [[ "${YOLUPAINTER_GPU:-1}" != 0 && -e /dev/dxg && -d /usr/lib/wsl/lib ]]; then
  export GALLIUM_DRIVER=d3d12
  export LD_LIBRARY_PATH="/usr/lib/wsl/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  # Dockerfile でビルドした新しい Mesa（d3d12 が OpenGL 4.6 を出す）があれば優先する。
  # システムの Mesa 23.2 は 4.2 止まりで、Unity が CopyTexture と compute shader を無効にする。
  # YOLUPAINTER_MESA=system でシステムの Mesa に戻せる。
  # 比較ビルドは /tmp などに置き、同梱の /opt を変更せず選ぶ。
  mesa_prefix="${YOLUPAINTER_MESA_PREFIX:-/opt/mesa-d3d12}"
  if [[ "${YOLUPAINTER_MESA:-bundled}" != system && -f "$mesa_prefix/lib/libGLX_mesa.so.0" ]]; then
    # Mesa 24.2 の libGLX_mesa は libgallium を直接リンクするので、DRI ドライバの置き場は要らない。
    export LD_LIBRARY_PATH="$mesa_prefix/lib:$LD_LIBRARY_PATH"
    export __GLX_VENDOR_LIBRARY_NAME=mesa
  fi
  # 同梱パッチが解釈する、3 系統それぞれの未使用バッファーキャッシュ上限（MiB）。
  # 既定は上流と同じ 512。実測して選ぶ（64 は GLX で比較、Unity は未確認）。
  export D3D12_BUFFER_CACHE_MB="${D3D12_BUFFER_CACHE_MB:-${YOLUPAINTER_GPU_CACHE_MB:-512}}"
  # 同梱の診断パッチ。DRED をデバイス作成前に設定し、デバイス消失と HRESULT をログに残す。
  export D3D12_DEVICE_DIAGNOSTICS="${D3D12_DEVICE_DIAGNOSTICS:-1}"
  # 台をまたいで GPU の重い仕事（メッシュマップのベイクの Dispatch）を 1 つずつにする（2026-10-03: 4 つの Unity が同時に GPU のベイクを
  # すると、GPU のドライバの層でまとめて落ちた。Editor/Gpu/GpuHeavyWorkGate.cs が読む。空にすれば止める）
  export YOLUPAINTER_GPU_HEAVY_LOCK="${YOLUPAINTER_GPU_HEAVY_LOCK-$HOME/.cache/yolupainter-tests/gpu-heavy.lock}"
  [[ -z "$YOLUPAINTER_GPU_HEAVY_LOCK" ]] || mkdir -p "$(dirname "$YOLUPAINTER_GPU_HEAVY_LOCK")"
  # GPU のベイク（compute のレイ）を止めて CPU で焼く（2026-10-03: WSL の更新の後、1 台だけでも GPU のベイクの中で GPU のデバイスが消えて
  # Unity が落ちるようになった。原因が分かるまで。GPU のベイクの試験は「使えない」で飛ぶ。YOLUPAINTER_GPU_BAKE_OFF= で戻す）
  export YOLUPAINTER_GPU_BAKE_OFF="${YOLUPAINTER_GPU_BAKE_OFF-1}"
  # 複数の GPU があるときは MESA_D3D12_DEFAULT_ADAPTER_NAME（部分一致）で選べる。
fi

# runners.conf の台の番号（0 を除く）とモード
runner_numbers() { sed -n 's/^\([0-9][0-9]*\)[[:space:]].*/\1/p' "$SCRIPT_DIR/runners.conf" 2>/dev/null; }
runner_conf_mode() { sed -n "s/^$1[[:space:]][[:space:]]*\([a-z-]*\).*/\1/p" "$SCRIPT_DIR/runners.conf" 2>/dev/null | head -1; }
# 台の常駐 Unity の今のモード（gui / batch-gl / batch）。動いていなければ down。
runner_live_mode() {
  local pidf; pidf="$(runner_project "$1")/TestDaemon/daemon.pid"
  [[ -f "$pidf" ]] && kill -0 "$(cat "$pidf")" 2>/dev/null || { echo down; return; }
  local args; args="$(ps -o args= -p "$(cat "$pidf")" 2>/dev/null)"
  if [[ "$args" == *-nographics* ]]; then echo batch; elif [[ "$args" == *-batchmode* ]]; then echo batch-gl; else echo gui; fi
}

info()  { printf '\033[36m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[33m警告:\033[0m %s\n' "$*" >&2; }
die()   { printf '\033[31mエラー:\033[0m %s\n' "$*" >&2; exit 1; }

have_license() {
  [[ -s "$LICENSE_SYSTEM_FILE" || -s "$LICENSE_USER_FILE" ]]
}

# 実際に見つかったライセンスファイルのパス
license_file() {
  if   [[ -s "$LICENSE_SYSTEM_FILE" ]]; then echo "$LICENSE_SYSTEM_FILE"
  elif [[ -s "$LICENSE_USER_FILE"   ]]; then echo "$LICENSE_USER_FILE"
  fi
}

# ボリュームに保管してある .ulf を Unity が読む場所へ戻す。
# machine-id はベースイメージで固定されているので、再ビルド後もそのまま通る。
restore_license() {
  if [[ -s "$LICENSE_STORE_FILE" ]] && ! have_license; then
    # Unity 2022.3 が実際に読み書きするのはユーザー側。system 側は他バージョン
    # 向けの保険として両方に置く。
    mkdir -p "$(dirname "$LICENSE_USER_FILE")" "$(dirname "$LICENSE_SYSTEM_FILE")"
    cp "$LICENSE_STORE_FILE" "$LICENSE_USER_FILE"
    cp "$LICENSE_STORE_FILE" "$LICENSE_SYSTEM_FILE"
    info "保管してあったライセンスを復元した: $LICENSE_USER_FILE"
  fi
}

# 有効化された .ulf をボリュームへ控える。ここに残しておけば再ビルドしても
# restore_license が拾ってくれる。
store_license() {
  local src
  src="$(license_file)"
  [[ -n "$src" ]] || return 1
  mkdir -p "$LICENSE_STORE"
  cp "$src" "$LICENSE_STORE_FILE"
  info "控えを保存した: $LICENSE_STORE_FILE"
}

license_hint() {
  cat >&2 <<'EOS'

Unity のライセンスがまだ有効化されていない。1 回だけ次のどちらかが要る。

[A] Unity ID でログインして有効化する（ブラウザ不要・こちらが速い）

     export UNITY_EMAIL='you@example.com'
     read -rs UNITY_PASSWORD && export UNITY_PASSWORD
     .devcontainer/unity/activate-license.sh --login

  2 要素認証を有効にしていると通らない。その場合は [B]。
  シートを 1 つ消費するので、コンテナを捨てる前に --return で返却すること。

[B] .alf / .ulf を手動で交換する

  1. .devcontainer/unity/activate-license.sh --create-alf
     → /workspace/temp~/ に Unity_v2022.3.22f1.alf が出る（ホストからも見える）
  2. https://license.unity3d.com/manual に .alf をアップロード
     ※ Unity は Personal の選択肢を CSS で隠している。シリアル入力欄しか
       出ない場合は、その欄を右クリック →「検証」→ Elements で
       `option-personal` を検索し、
       <div class="option option-personal clear" style="display: none;">
       の style="display: none;" を消すとラジオが現れる。
  3. 落ちてきた .ulf を /workspace/temp~/ に置いて:
     .devcontainer/unity/activate-license.sh --install temp~/Unity_v2022.x.ulf

temp~/ は .gitignore 済み、かつ Unity のインポート対象外なので安全。
EOS
}
