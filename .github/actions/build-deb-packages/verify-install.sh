#!/usr/bin/env bash
# Installs the built deb in clean containers and starts the CLI once in each.
# The .NET runtime loads ICU at run time, so no dependency scan sees it: without a declared
# ICU package a minimal install succeeds and the first start aborts.
# Usage: verify-install.sh <folder with the built deb> [image ...]
set -euo pipefail

# On Windows Git Bash, pwd -W gives C:/... and MSYS_NO_PATHCONV stops the rewrite of the -v argument;
# on Linux pwd -W fails and plain pwd is used.
export MSYS_NO_PATHCONV=1
DEB_DIR="$(cd "${1:?usage: verify-install.sh <folder with the built deb> [image ...]}" && { pwd -W 2> /dev/null || pwd; })"
shift
if [ "$#" -gt 0 ]; then IMAGES=("$@"); else IMAGES=(ubuntu:24.04 debian:12); fi

for image in "${IMAGES[@]}"; do
  docker run --rm -e DEBIAN_FRONTEND=noninteractive -e TZ=Etc/UTC -v "${DEB_DIR}:/pkg:ro" "${image}" bash -c '
    set -euo pipefail
    # The pool also holds nomercy_latest_amd64.deb, a link to the same package.
    deb="$(find /pkg -maxdepth 1 -name "*.deb" ! -name "*_latest_*" | head -n 1)"
    [ -n "${deb}" ] || { echo "no deb in the folder" >&2; exit 1; }
    dpkg-deb -f "${deb}" Depends Recommends | grep -q libicu || { echo "the deb declares no ICU package" >&2; exit 1; }
    apt-get update -qq
    apt-get install -y -qq "${deb}" > /dev/null
    /opt/nomercy/nomercy --help > /dev/null
    echo "nomercy installs and the CLI starts on $(. /etc/os-release && echo "${PRETTY_NAME}")"
  '
done
