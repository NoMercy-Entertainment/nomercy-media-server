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
# Validate desktop entries against the files installed in a Linux package root.
# Usage: verify-desktop-entries.sh <package root>
set -euo pipefail

package_root="${1:?usage: verify-desktop-entries.sh <package root>}"
applications="${package_root}/usr/share/applications"
icons="${package_root}/usr/share/icons/hicolor/scalable/apps"
shopt -s nullglob
entries=("${applications}"/*.desktop)
[ "${#entries[@]}" -gt 0 ] || { echo "no desktop entries in ${applications}" >&2; exit 1; }
failures=0

for entry in "${entries[@]}"; do
  if ! desktop-file-validate "${entry}"; then failures=$((failures + 1)); fi
  exec_target="$(sed -n 's/^Exec=\([^ ]*\).*/\1/p' "${entry}")"
  icon_name="$(sed -n 's/^Icon=//p' "${entry}")"
  [ -n "${exec_target}" ] || { echo "missing Exec in ${entry}" >&2; exit 1; }
  [ -n "${icon_name}" ] || { echo "missing Icon in ${entry}" >&2; exit 1; }

  case "${exec_target}" in
    /*) installed_command="${package_root}${exec_target}" ;;
    *) installed_command="${package_root}/usr/bin/${exec_target}" ;;
  esac
  if [ -L "${installed_command}" ]; then
    link_target="$(readlink "${installed_command}")"
    case "${link_target}" in
      /*) installed_command="${package_root}${link_target}" ;;
      *) installed_command="$(dirname "${installed_command}")/${link_target}" ;;
    esac
  fi
  if [ ! -f "${installed_command}" ]; then
    echo "unresolved Exec=${exec_target} in ${entry}" >&2
    failures=$((failures + 1))
  fi

  case "${icon_name}" in
    /*) installed_icon="${package_root}${icon_name}" ;;
    *) installed_icon="${icons}/${icon_name}.png" ;;
  esac
  if [ ! -f "${installed_icon}" ]; then
    echo "unresolved Icon=${icon_name} in ${entry}" >&2
    failures=$((failures + 1))
  fi
done

echo "Checked ${#entries[@]} desktop entries: ${failures} failures"
[ "${failures}" -eq 0 ]
