#!/bin/bash
set -euo pipefail

VERSION=""
PAYLOAD_PATH=""
OUTPUT_PATH="./output"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --payload-path) PAYLOAD_PATH="$2"; shift 2 ;;
        --output-path) OUTPUT_PATH="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

if [[ -z "$VERSION" || -z "$PAYLOAD_PATH" ]]; then
    echo "Usage: $0 --version <version> --payload-path <path> [--output-path <path>]"
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAGING="$(mktemp -d)"
COMPONENT_PKGS="$(mktemp -d)"

trap 'rm -rf "$STAGING" "$COMPONENT_PKGS"' EXIT

echo "=== Building NoMercy MediaServer macOS installer v${VERSION} ==="

# --- Component 1: Server (includes shared runtime) ---
echo "Staging Server component..."
SERVER_ROOT="${STAGING}/server"
APP_MACOS="${SERVER_ROOT}/Applications/NoMercy MediaServer.app/Contents/MacOS"
APP_RESOURCES="${SERVER_ROOT}/Applications/NoMercy MediaServer.app/Contents/Resources"
mkdir -p "${APP_MACOS}"
mkdir -p "${APP_RESOURCES}"

# Copy entire shared runtime payload into the .app bundle
cp -R "${PAYLOAD_PATH}/"* "${APP_MACOS}/"
chmod 755 "${APP_MACOS}/NoMercyMediaServer"
chmod 755 "${APP_MACOS}/NoMercyApp"

# Copy icon if available
if [[ -f "${SCRIPT_DIR}/../../assets/icons/icon.icns" ]]; then
    cp "${SCRIPT_DIR}/../../assets/icons/icon.icns" "${APP_RESOURCES}/AppIcon.icns"
fi

cat > "${SERVER_ROOT}/Applications/NoMercy MediaServer.app/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>NoMercyMediaServer</string>
    <key>CFBundleIdentifier</key>
    <string>tv.nomercy.mediaserver</string>
    <key>CFBundleName</key>
    <string>NoMercy MediaServer</string>
    <key>CFBundleDisplayName</key>
    <string>NoMercy MediaServer</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSLocalNetworkUsageDescription</key>
    <string>NoMercy MediaServer streams your library to the devices on your network.</string>
</dict>
</plist>
PLIST

pkgbuild --root "${SERVER_ROOT}" \
    --identifier tv.nomercy.mediaserver \
    --version "${VERSION}" \
    --install-location "/" \
    "${COMPONENT_PKGS}/server.pkg"

# --- Component 2: Service (LaunchAgent plist only — binary already in .app) ---
echo "Staging Service component..."
SERVICE_ROOT="${STAGING}/service"
mkdir -p "${SERVICE_ROOT}/Library/LaunchAgents"

cp "${SCRIPT_DIR}/tv.nomercy.mediaserver.service.plist" \
   "${SERVICE_ROOT}/Library/LaunchAgents/tv.nomercy.mediaserver.service.plist"

pkgbuild --root "${SERVICE_ROOT}" \
    --identifier tv.nomercy.mediaserver.service \
    --version "${VERSION}" \
    --install-location "/" \
    "${COMPONENT_PKGS}/service.pkg"

# --- Component 3: CLI (symlink to binary inside .app) ---
echo "Staging CLI component..."
CLI_ROOT="${STAGING}/cli"
mkdir -p "${CLI_ROOT}/usr/local/bin"

ln -s "/Applications/NoMercy MediaServer.app/Contents/MacOS/nomercy" \
   "${CLI_ROOT}/usr/local/bin/nomercy"

# The CLI component installs last. Its postinstall points the LaunchAgent and the
# CLI link at the folder the app really landed in (a user-folder install uses
# ~/Applications, not /Applications).
pkgbuild --root "${CLI_ROOT}" \
    --scripts "${SCRIPT_DIR}/scripts" \
    --identifier tv.nomercy.cli \
    --version "${VERSION}" \
    --install-location "/" \
    "${COMPONENT_PKGS}/cli.pkg"

# --- Build combined installer ---
echo "Building combined .pkg installer..."
mkdir -p "${OUTPUT_PATH}"

productbuild \
    --distribution "${SCRIPT_DIR}/distribution.xml" \
    --package-path "${COMPONENT_PKGS}" \
    --version "${VERSION}" \
    "${OUTPUT_PATH}/NoMercyMediaServer-${VERSION}-macos-x64-setup.pkg"

echo "=== macOS installer built: ${OUTPUT_PATH}/NoMercyMediaServer-${VERSION}-macos-x64-setup.pkg ==="
