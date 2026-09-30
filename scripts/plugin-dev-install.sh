#!/usr/bin/env bash
# Build a plugin, put it where the dev server looks, and restart the server.
#
# Retyped by hand every time until now, and a retyped chain drops a step: the
# server holds the plugin DLL open, so a build that looks green can have copied
# nothing, and the next page load still shows the previous build. That failure
# is silent — it reads as "the fix did not work".
#
#   scripts/plugin-dev-install.sh <path-to-plugin-csproj>
#   scripts/plugin-dev-install.sh <path-to-plugin-csproj> --no-restart
set -uo pipefail

project="${1:-}"
if [ -z "$project" ] || [ ! -f "$project" ]; then
  echo "usage: $0 <plugin.csproj> [--no-restart]" >&2
  exit 2
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
plugins_dir="${LOCALAPPDATA:-$HOME/.local/share}/NoMercy_dev/plugins"
server_exe="$here/src/NoMercy.Service/bin/Debug/net10.0/NoMercyMediaServer.exe"
assembly="$(basename "$project" .csproj)"
target="$plugins_dir/$assembly"

if [ ! -d "$target" ]; then
  echo "not installed: $target" >&2
  echo "drop the plugin folder there once, then this keeps it current." >&2
  exit 2
fi

echo "== stopping the dev server =="
# By its image name: the PID changes every restart, and a stale one silently
# matches nothing while the real process keeps the DLL locked.
taskkill //IM NoMercyMediaServer.exe //F >/dev/null 2>&1 && sleep 2

echo "== building $assembly =="
if ! dotnet build "$project" -c Release --nologo; then
  echo "   FAILED: build" >&2
  exit 1
fi

built="$(dirname "$project")/bin/Release/net10.0/$assembly.dll"
if [ ! -f "$built" ]; then
  echo "   FAILED: no $built" >&2
  exit 1
fi

cp "$built" "$target/" || exit 1

# The manifest travels with the assembly. A capability added in plugin.json and
# left behind in the source tree is a plugin that loads and then does nothing.
manifest="$(dirname "$project")/plugin.json"
[ -f "$manifest" ] && cp "$manifest" "$target/"

lang="$(dirname "$project")/lang"
[ -d "$lang" ] && cp -r "$lang/." "$target/lang/" 2>/dev/null

# The server checks every file against the record an install writes beside
# the plugin, and a folder whose files differ from it is refused at the next
# boot. This copy is the install, so it writes the record: one line per file,
# `<sha256>  <relative path>`, sorted, the format PluginFileManifest reads.
(
  cd "$target" \
    && find . -type f ! -name .files.sha256 ! -name .sideloaded \
    | sed 's|^\./||' | LC_ALL=C sort \
    | xargs -r -d '\n' sha256sum --binary \
    | sed 's/^\([0-9a-f]\{64\}\) \*/\1  /' > .files.sha256
) || exit 1

echo "== installed to $target =="
ls -l "$target"

if [ "${2:-}" = "--no-restart" ]; then
  exit 0
fi

# The server binary, not just the plugin. This script restarted whatever was
# last built, so a contract change in the server source left the old host
# running: it loaded the previous build of a plugin and silently dropped the new
# one, which reads as "my plugin stopped loading" rather than "the host is
# stale".
echo "== building the server =="
if ! dotnet build "$here/src/NoMercy.Service/NoMercy.Service.csproj" --nologo -v q; then
  echo "   FAILED: server build" >&2
  exit 1
fi

echo "== starting the dev server =="
nohup "$server_exe" --dev --loglevel=verbose > "$here/plugin-dev-install.log" 2>&1 &
echo "log: $here/plugin-dev-install.log"
