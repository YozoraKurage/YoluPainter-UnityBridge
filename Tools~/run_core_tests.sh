#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1; then
  if [ -x /workspace/shared/dotnet/dotnet ]; then DOTNET=/workspace/shared/dotnet/dotnet;
  else echo 'Install official .NET 8 SDK or set DOTNET to its executable.' >&2; exit 2; fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/tmp/dotnet-cli-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-/tmp/dotnet-nuget}"
"$DOTNET" run --project Tools~/CoreHarness/CoreHarness.csproj --configuration Release
