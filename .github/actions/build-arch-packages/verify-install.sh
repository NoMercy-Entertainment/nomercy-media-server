#!/usr/bin/env bash
# Installs the built Arch package in a clean container and starts the CLI once.
# The .NET runtime loads ICU at run time, so no dependency scan sees it: without a declared
# icu dependency a minimal install succeeds and the first start aborts.
# Usage: verify-install.sh <folder with the built .pkg.tar.zst> [image]
set -euo pipefail

# On Windows Git Bash, pwd -W gives C:/... and MSYS_NO_PATHCONV stops the rewrite of the -v argument;
# on Linux pwd -W fails and plain pwd is used.
export MSYS_NO_PATHCONV=1
PKG_DIR="$(cd "${1:?usage: verify-install.sh <folder with the built .pkg.tar.zst> [image]}" && { pwd -W 2> /dev/null || pwd; })"
IMAGE="${2:-archlinux:latest}"

docker run --rm -v "${PKG_DIR}:/pkg:ro" "${IMAGE}" bash -c '
  set -euo pipefail
  pacman-key --init > /dev/null 2>&1
  pacman -Sy --noconfirm > /dev/null
  pacman -U --noconfirm /pkg/*.pkg.tar.zst > /dev/null
  pacman -Qi nomercy | grep "^Depends On" | grep -qw icu || { echo "the package declares no icu dependency" >&2; exit 1; }
  /opt/nomercy/nomercy --help > /dev/null
  echo "nomercy installs and the CLI starts on $(. /etc/os-release && echo "${PRETTY_NAME}")"
'
