#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1; then
  if [ -x /workspace/shared/dotnet/dotnet ]; then export DOTNET=/workspace/shared/dotnet/dotnet;
  else echo 'Install official .NET 8 SDK or set DOTNET.' >&2; exit 2; fi
fi
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/tmp/dot-texture-painter-cli}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-/tmp/dot-texture-painter-nuget}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-/tmp/dot-texture-painter-nuget-http}"
export XDG_DATA_HOME="${XDG_DATA_HOME:-/tmp/dot-texture-painter-xdg}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p Validation~
./Tools~/run_core_tests.sh | tee Validation~/core-integration.log
"$DOTNET" build Tools~/CoreCompile/CoreCompile.csproj --configuration Release | tee Validation~/netstandard-compile.log
"$DOTNET" run --project Tools~/CoreNUnit/CoreNUnit.csproj --configuration Release -- --result=Validation~/core-psd-nunit.xml | tee Validation~/core-psd-nunit.log
DOTNET="$DOTNET" ./Tools~/GeometryHarness/run.sh | tee Validation~/geometry-adapter.log
"$DOTNET" run --project Tools~/SyntaxCheck/SyntaxCheck.csproj -- Editor Runtime Tests | tee Validation~/csharp-syntax.log
printf '\nPortable checks done. Unity Editor/API/shaders/device and Photoshop/CSP are separate unrun gates.\n'
