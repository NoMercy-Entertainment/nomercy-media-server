#!/usr/bin/env bash
# Tests for templates/DEBIAN/postinst and prerm with stubbed systemctl and loginctl.
# Run: bash .github/actions/build-deb-packages/tests/deb-scripts.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
failures=0
mkdir -p "$tmp/bin"

printf '#!/bin/sh\nif [ "$1" = list-users ]; then echo "1000 alice"; echo "1001 bob"; else echo "loginctl $*" >> "$STUB_LOG"; fi\n' > "$tmp/bin/loginctl"
printf '#!/bin/sh\necho "$*" >> "$STUB_LOG"\n' > "$tmp/bin/systemctl"
chmod +x "$tmp/bin/loginctl" "$tmp/bin/systemctl"

# run <script> <args...>: output in $tmp/out, systemctl calls in $tmp/calls
run() {
  : > "$tmp/calls"
  STUB_LOG="$tmp/calls" PATH="$tmp/bin:$PATH" sh "$here/templates/DEBIAN/$1" "${@:2}" > "$tmp/out" 2>&1
}

# check <name> <file> <text> present|absent
check() {
  local found=1
  grep -F -- "$3" "$2" > /dev/null && found=0
  if { [ "$4" = present ] && [ "$found" -eq 0 ]; } || { [ "$4" = absent ] && [ "$found" -ne 0 ]; }; then
    echo "ok   $1"
  else
    echo "FAIL $1"
    failures=$((failures + 1))
  fi
}

run postinst configure
check "fresh install prints the exact next step" "$tmp/out" 'systemctl --user enable --now nomercymediaserver.service' present
check "fresh install restarts nothing" "$tmp/calls" try-restart absent

check "fresh install names the linger step" "$tmp/out" 'loginctl enable-linger' present
check "fresh install without a sudo user enables no linger itself" "$tmp/calls" enable-linger absent

SUDO_USER=carol run postinst configure
check "fresh install via sudo keeps the server running after logout" "$tmp/calls" 'loginctl enable-linger carol' present

run postinst configure 1.0.0
check "upgrade does not change lingering" "$tmp/calls" enable-linger absent
check "upgrade reloads units for alice" "$tmp/calls" '--user --machine=alice@ daemon-reload' present
check "upgrade restarts a running server for alice" "$tmp/calls" '--user --machine=alice@ try-restart nomercymediaserver.service' present
check "upgrade restarts a running server for bob" "$tmp/calls" '--user --machine=bob@ try-restart nomercymediaserver.service' present
check "upgrade runs no user command as root without a target" "$tmp/calls" '--user daemon-reload' absent

run prerm upgrade 1.0.1
check "prerm on upgrade keeps the autostart setting" "$tmp/calls" disable absent

run prerm remove
check "prerm on remove stops the server for alice" "$tmp/calls" '--user --machine=alice@ stop nomercymediaserver.service' present
check "prerm on remove disables the server for alice" "$tmp/calls" '--user --machine=alice@ disable nomercymediaserver.service' present

if [ "$failures" -eq 0 ]; then echo "all deb script tests passed"; else echo "$failures failed"; exit 1; fi
