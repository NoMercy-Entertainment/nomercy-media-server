#!/usr/bin/env bash
# Builds the real templates/nomercy.spec around a tiny fake payload in a Fedora container and checks
# the package metadata that `rpm -qi` and dnf show (Vendor, Packager, URL, License, bug URL).
# Run: bash .github/actions/build-rpm-packages/verify-metadata.test.sh [image]
set -euo pipefail
export MSYS_NO_PATHCONV=1
here="$(cd "$(dirname "$0")" && { pwd -W 2> /dev/null || pwd; })"
IMAGE="${1:-fedora:latest}"

docker run --rm -v "${here}/templates:/templates:ro" "${IMAGE}" bash -c '
  set -euo pipefail
  dnf install -y -q rpm-build > /dev/null
  mkdir -p ~/rpmbuild/{SOURCES,SPECS} /tmp/payload
  for f in NoMercyMediaServer NoMercyApp NoMercyLauncher nomercy; do printf "#!/bin/sh\n" > /tmp/payload/$f; done
  tar -czf ~/rpmbuild/SOURCES/nomercy-payload.tar.gz -C /tmp/payload .
  touch ~/rpmbuild/SOURCES/NoMercy-MediaServer.desktop ~/rpmbuild/SOURCES/icon.png
  cp /templates/*.service /templates/README ~/rpmbuild/SOURCES/
  sed -e "s/{{VERSION}}/1.2.3/g" -e "s/{{CHANGELOG_DATE}}/Mon Jan 01 2024/g" /templates/nomercy.spec > ~/rpmbuild/SPECS/nomercy.spec
  # The fake payload has no ELF files, so rpm finds no debug info: skip the debug subpackages.
  rpmbuild -bb --define "debug_package %{nil}" ~/rpmbuild/SPECS/nomercy.spec > /tmp/rpmbuild.log 2>&1 || { tail -20 /tmp/rpmbuild.log; exit 1; }
  rpm_file="$(find ~/rpmbuild/RPMS -name "*.rpm" | head -n 1)"
  fail=0
  check() { # <label> <rpm tag> <expected>
    actual="$(rpm -qp --qf "%{$2}" "${rpm_file}")"
    if [ "${actual}" = "$3" ]; then echo "ok   $1: ${actual}"; else echo "FAIL $1: got [${actual}], want [$3]"; fail=1; fi
  }
  check vendor VENDOR "NoMercy Entertainment"
  check packager PACKAGER "NoMercy Entertainment <support@nomercy.tv>"
  check url URL "https://nomercy.tv"
  check bugurl BUGURL "https://github.com/NoMercy-Entertainment/nomercy-media-server/issues"
  check license LICENSE "Proprietary"
  exit ${fail}
'
