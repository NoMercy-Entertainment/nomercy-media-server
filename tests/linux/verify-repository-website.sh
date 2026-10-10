#!/usr/bin/env bash
# -----------------------------------------------------------------------------
#  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
#
#  This file is part of NoMercy MediaServer, source-available software (NOT open
#  source). Personal use and contributions are welcome; distribution, resale,
#  relicensing, and commercial exploitation are prohibited without explicit
#  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
#
#  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
# -----------------------------------------------------------------------------
# Run: bash tests/linux/verify-repository-website.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
template="${root}/.github/actions/generate-repository-website/templates/index.html"
html="$(sed 's/{{VERSION}}/1.2.3/g' "${template}")"
failures=0

expect_count() {
  local description="$1"
  local expected="$2"
  local pattern="$3"
  local actual
  actual="$(printf '%s\n' "${html}" | grep -Fc "${pattern}" || true)"
  if [ "${actual}" -eq "${expected}" ]; then
    echo "ok    ${description}: ${actual}/${expected}"
  else
    echo "FAIL  ${description}: ${actual}/${expected}"
    failures=$((failures + 1))
  fi
}

expect_count 'APT install command' 1 '<code>sudo apt install nomercy</code>'
expect_count 'DNF install command' 1 '<code>sudo dnf install nomercy</code>'
expect_count 'Pacman install command' 1 '<code>sudo pacman -S nomercy</code>'
expect_count 'Package card labels' 2 '<div class="package-size">Included in nomercy package</div>'

echo "Checked 4 website assertions: ${failures} failure(s)"
[ "${failures}" -eq 0 ]
