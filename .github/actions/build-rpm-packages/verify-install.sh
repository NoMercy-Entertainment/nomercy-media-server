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
  # Capture the list first: under pipefail, grep -q exits at the first match and rpm then dies of
  # SIGPIPE (exit 141), which failed this check on an rpm that does declare libicu (2026-10-14).
  # A self-hosted runner can keep an older build in the folder (0.1.579 next to 0.1.580 made
  # dnf refuse both, 2026-10-15), so test the newest version only.
  rpm_file="$(find /pkg -maxdepth 1 -name "*.rpm" | sort -V | tail -n 1)"
  [ -n "${rpm_file}" ] || { echo "no rpm in the folder" >&2; exit 1; }
  requires="$(rpm -qp --requires "${rpm_file}")"
  grep -qx "libicu" <<< "${requires}" || { echo "the rpm declares no libicu Requires" >&2; exit 1; }
  dnf install -y -q "${rpm_file}"
  /opt/nomercy/nomercy --help > /dev/null
  echo "nomercy installs and the CLI starts on $(. /etc/os-release && echo "${PRETTY_NAME}")"
'
