#!/usr/bin/env bash
# Installs the built rpm in a clean Fedora container and starts the CLI once.
# rpmbuild generates a Requires from every bundled ELF file, so a file that never runs on
# x86_64 (an arm64 native lib) can make `dnf install nomercy` fail for every user; and the
# .NET runtime aborts at start when a library it loads at run time (ICU) is missing.
# Usage: verify-install.sh <folder with the built rpm> [image]
set -euo pipefail

# On Windows Git Bash, pwd -W gives C:/... and MSYS_NO_PATHCONV stops the rewrite of the -v argument;
# on Linux pwd -W fails and plain pwd is used.
export MSYS_NO_PATHCONV=1
RPM_DIR="$(cd "${1:?usage: verify-install.sh <folder with the built rpm> [image]}" && { pwd -W 2> /dev/null || pwd; })"
IMAGE="${2:-fedora:latest}"

docker run --rm -v "${RPM_DIR}:/pkg:ro" "${IMAGE}" bash -c '
  set -euo pipefail
  # ICU usually arrives through other packages, so the start alone cannot catch a missing declaration.
  rpm -qp --requires /pkg/*.rpm | grep -qx "libicu" || { echo "the rpm declares no libicu Requires" >&2; exit 1; }
  dnf install -y -q /pkg/*.rpm
  /opt/nomercy/nomercy --help > /dev/null
  echo "nomercy installs and the CLI starts on $(. /etc/os-release && echo "${PRETTY_NAME}")"
'
