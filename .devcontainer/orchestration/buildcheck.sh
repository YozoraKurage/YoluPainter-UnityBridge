#!/bin/bash
# usage: buildcheck.sh <tree dir> — Unity の csc とテストプロジェクトの参照で、<tree dir> のパッケージの Core・Editor・Tests（と Burst）を組む（数十秒。Unity は要らない）
set -e
TREE=$(realpath "$1"); mkdir -p /tmp/yolupainter-buildcheck; OUT=$(mktemp -d /tmp/yolupainter-buildcheck/out.XXXX); trap 'rm -rf "$OUT"' EXIT
DAG=$(ls -d /home/node/unity-testproject/Library/Bee/artifacts/*.dag | head -1)
cd /home/node/unity-testproject
build() { # name, source dirs...
  local name=$1; shift
  local rsp=$OUT/$name.rsp
  grep -v '\.cs"$' "$DAG/$name.rsp" | grep -v '^-out:\|^-refout:\|Yozolab.YoluPainter' > "$rsp"
  echo "-out:\"$OUT/$name.dll\"" >> "$rsp"
  for dep in "${DEPS[@]}"; do echo "-r:\"$OUT/$dep.dll\"" >> "$rsp"; done
  for d in "$@"; do if [ "$d" = "Editor" ]; then find "$TREE/$d" -name '*.cs' -not -path "$TREE/Editor/Api/*"; else find "$TREE/$d" -name '*.cs'; fi | sed 's/.*/"&"/' >> "$rsp"; done
  if ! /opt/unity/Editor/Data/NetCoreRuntime/dotnet exec /opt/unity/Editor/Data/DotNetSdkRoslyn/csc.dll /nostdlib /noconfig "@$rsp" > "$OUT/$name.log" 2>&1; then
    echo "FAIL $name"; grep -E "error CS" "$OUT/$name.log" | sed "s|$TREE/||" | head -20; exit 1; fi
  echo "ok $name ($(grep -c 'warning CS' "$OUT/$name.log" || true) warnings)"
}
DEPS=(); build Yozolab.YoluPainter.Core Runtime/Core
if [ -d "$TREE/Editor/Api" ]; then DEPS=(Yozolab.YoluPainter.Core); build Yozolab.YoluPainter.Api Editor/Api; API=(Yozolab.YoluPainter.Api); else API=(); fi
DEPS=(Yozolab.YoluPainter.Core "${API[@]}"); build Yozolab.YoluPainter.Editor Editor
DEPS=(Yozolab.YoluPainter.Core "${API[@]}" Yozolab.YoluPainter.Editor); build Yozolab.YoluPainter.Tests Tests/Editor
# Burst の版（Runtime/Burst、com.unity.burst があるときだけ Unity が組む）: テストプロジェクトがそれを組んだことがあれば同じ参照で組む
if [ -f "$DAG/Yozolab.YoluPainter.Core.Burst.rsp" ] && [ -d "$TREE/Runtime/Burst" ]; then
  DEPS=(Yozolab.YoluPainter.Core); build Yozolab.YoluPainter.Core.Burst Runtime/Burst
fi
