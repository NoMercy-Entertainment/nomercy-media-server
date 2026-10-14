#!/usr/bin/env bash
# Format, build and test, in that order, on an explicit list of files.
#
# The order matters and is the reason this is a script rather than three
# commands: csharpier rewrites files, so formatting after a green test run
# leaves the tested bytes and the committed bytes different. Formatting first
# means what was tested is what lands.
#
# A file list, never the project: a whole-project format touches uncommitted
# work in progress that has nothing to do with the change being gated.
#
#   scripts/server-gate.sh src/NoMercy.Design/NmAppComponents.cs tests/.../FooTests.cs
#   scripts/server-gate.sh --filter PluginDesign src/NoMercy.Plugins.Abstractions/PluginDesign.cs
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
filter=""

if [ "${1:-}" = "--filter" ]; then
  filter="$2"
  shift 2
fi

if [ "$#" -eq 0 ]; then
  echo "usage: $0 [--filter <test-name-fragment>] <file> [file...]" >&2
  exit 2
fi

step() {
  local label="$1"
  shift
  echo "== $label =="
  if ! "$@"; then
    echo "   FAILED: $label" >&2
    exit 1
  fi
}

# The server holds its own DLLs open, so a build against a running instance
# fails on a file lock that reads as an unrelated compiler error.
if tasklist //FI "IMAGENAME eq NoMercyMediaServer.exe" 2>/dev/null | grep NoMercyMediaServer > /dev/null; then
  echo "== stopping the running server =="
  taskkill //IM NoMercyMediaServer.exe //F >/dev/null 2>&1
fi

step format dotnet csharpier format "$@"
step build dotnet build "$here/NoMercy.Server.sln" --nologo -v q

if [ -n "$filter" ]; then
  step "test ($filter)" dotnet test "$here/NoMercy.Server.sln" --nologo --filter "FullyQualifiedName~$filter"
else
  step test dotnet test "$here/NoMercy.Server.sln" --nologo
fi

echo "== green =="
