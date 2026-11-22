#!/bin/bash
# Test for packaging/macos/scripts/postinstall (issue #661).
# Simulates a pkg installed into a user's home folder: the files carry the
# default /Applications paths, the app lives in $HOME/Applications.
# Runs anywhere bash runs. Prints one summary line: "postinstall tests: N/M passed".
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="${HERE}/../scripts/postinstall"
PLIST_SRC="${HERE}/../tv.nomercy.mediaserver.service.plist"
APP_NAME="NoMercy MediaServer.app"
PLIST_NAME="tv.nomercy.mediaserver.service.plist"
TOTAL=0
PASSED=0

check() { # name, condition result (0 = ok)
    TOTAL=$((TOTAL + 1))
    if [[ "$2" -eq 0 ]]; then PASSED=$((PASSED + 1)); echo "ok   - $1"; else echo "FAIL - $1"; fi
}

new_root() {
    local root
    root="$(mktemp -d)"
    mkdir -p "${root}/home" "${root}/system"
    echo "$root"
}

run_postinstall() { # root, installer target
    NM_HOME="$1/home" NM_SYSTEM_ROOT="$1/system" bash "$SCRIPT" "pkg" "$2" "/" "$2" >/dev/null 2>&1
}

# --- Case 1: user-folder install ---
ROOT="$(new_root)"
HOME_APP="${ROOT}/home/Applications/${APP_NAME}"
mkdir -p "${HOME_APP}/Contents/MacOS" "${ROOT}/home/Library/LaunchAgents" "${ROOT}/home/usr/local/bin"
cp "$PLIST_SRC" "${ROOT}/home/Library/LaunchAgents/${PLIST_NAME}"
touch "${ROOT}/old-target" "${HOME_APP}/Contents/MacOS/nomercy"
ln -s "${ROOT}/old-target" "${ROOT}/home/usr/local/bin/nomercy"

run_postinstall "$ROOT" "${ROOT}/home"
check "postinstall exits 0 for a user-folder install" $?

grep -qF "${HOME_APP}/Contents/MacOS/NoMercyLauncher" "${ROOT}/home/Library/LaunchAgents/${PLIST_NAME}"
check "LaunchAgent points at the home-folder app" $?

grep -qF "<string>/Applications/" "${ROOT}/home/Library/LaunchAgents/${PLIST_NAME}"
check "LaunchAgent no longer points at /Applications" $((1 - $?))

# Git Bash on Windows copies instead of linking; the link check needs a real symlink (macOS, Linux CI).
if [[ -L "${ROOT}/home/usr/local/bin/nomercy" ]]; then
    [[ "$(readlink "${ROOT}/home/usr/local/bin/nomercy")" == "${HOME_APP}/Contents/MacOS/nomercy" ]]
    check "CLI link points at the home-folder app" $?
else
    echo "skip - CLI link check (this shell cannot create symlinks)"
fi

# --- Case 2: system install stays unchanged ---
ROOT2="$(new_root)"
SYS_APP="${ROOT2}/system/Applications/${APP_NAME}"
mkdir -p "${SYS_APP}/Contents/MacOS" "${ROOT2}/system/Library/LaunchAgents"
cp "$PLIST_SRC" "${ROOT2}/system/Library/LaunchAgents/${PLIST_NAME}"
BEFORE="$(cat "${ROOT2}/system/Library/LaunchAgents/${PLIST_NAME}")"
run_postinstall "$ROOT2" "${ROOT2}/system"
check "postinstall exits 0 for a system install" $?
[[ "$(cat "${ROOT2}/system/Library/LaunchAgents/${PLIST_NAME}")" == "$BEFORE" ]]
check "system install keeps its LaunchAgent unchanged" $?

# --- Case 3: path with an ampersand is escaped in the plist ---
ROOT3="$(new_root)"
AMP_HOME="${ROOT3}/home & more"
mkdir -p "${AMP_HOME}/Applications/${APP_NAME}/Contents/MacOS" "${AMP_HOME}/Library/LaunchAgents"
cp "$PLIST_SRC" "${AMP_HOME}/Library/LaunchAgents/${PLIST_NAME}"
NM_HOME="$AMP_HOME" NM_SYSTEM_ROOT="${ROOT3}/system" bash "$SCRIPT" pkg "$AMP_HOME" / "$AMP_HOME" >/dev/null 2>&1
grep -qF "home &amp; more/Applications/${APP_NAME}/Contents/MacOS/NoMercyLauncher" "${AMP_HOME}/Library/LaunchAgents/${PLIST_NAME}"
check "ampersand in the install path is XML-escaped" $?

# --- Case 4: nothing installed is not an error ---
ROOT4="$(new_root)"
run_postinstall "$ROOT4" "${ROOT4}/home"
check "postinstall exits 0 when no app is found" $?

# --- Case 5: build script wires the scripts into the pkg ---
grep -q -- "--scripts" "${HERE}/../build-pkg.sh"
check "build-pkg.sh passes --scripts to pkgbuild" $?

rm -rf "$ROOT" "$ROOT2" "$ROOT3" "$ROOT4"
echo "postinstall tests: ${PASSED}/${TOTAL} passed"
[[ "$PASSED" -eq "$TOTAL" ]]
