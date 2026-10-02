#!/usr/bin/env bash
# Pure C# math checks only. These adapters DO NOT validate Unity APIs, rendering, shaders, or EditMode.
set -euo pipefail
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-${TMPDIR:-/tmp}/dot-texture-painter-cli}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
python3 - <<'PY'
from pathlib import Path
source = Path('../../Tests/Editor/GeometryTests.cs').read_text()
marker = '        [Test]\n        public void LoadClonesOnlyRenderDataAndLeavesSourceUnchanged()'
# Keep the identical pure-geometry test methods. Unity ownership/load tests require real Unity.
Path('GeometryTests.generated.cs').write_text(source[:source.index(marker)] + '    }\n}\n')
PY
"$DOTNET" run --project GeometryHarness.csproj --configuration Release
