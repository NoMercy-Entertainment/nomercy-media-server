#!/usr/bin/env bash
# Rebuild NoMercy.Service and relaunch the dev server, killing any instance
# that's locking the build output first. Bash tool cannot background a
# process past its own call's lifetime with plain `&`; nohup + disown is
# required, and the .NET.Service.csproj build must happen with the old
# process already dead or MSBuild's PDB copy step retries and fails.
set -euo pipefail

BIN_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/src/NoMercy.Service/bin/Debug/net10.0"
SERVICE_CSPROJ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/src/NoMercy.Service/NoMercy.Service.csproj"

pid=$(wmic process where "name='NoMercyMediaServer.exe'" get ProcessId 2>/dev/null | grep -E '^[0-9]+' || true)
if [ -n "$pid" ]; then
  echo "Killing existing server (PID $pid)"
  taskkill //F //PID "$pid" >/dev/null 2>&1 || true
  sleep 2
fi

echo "Building..."
dotnet build "$SERVICE_CSPROJ" -v q

echo "Launching..."
cd "$BIN_DIR"
nohup ./NoMercyMediaServer.exe --dev --loglevel=verbose > "dev-run-$(date +%s).log" 2>&1 &
disown

for i in $(seq 1 15); do
  if nomercy status 2>&1 | grep "running" > /dev/null; then
    echo "up"
    nomercy status
    exit 0
  fi
  sleep 2
done

echo "server did not come up within 30s" >&2
exit 1
