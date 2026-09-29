#!/usr/bin/env bash
# Prints the version the next nightly build gets.
#
#   next-version.sh <latest-tag> <release-line>
#
# <latest-tag>    newest published tag, with or without a leading v (0.1.532)
# <release-line>  MAJOR.MINOR from .github/release-line (0.1, 1.0, ...)
#
# Same line as the latest tag: bump the patch (0.1.532 -> 0.1.533).
# Release line ahead of the latest tag: start the line at .0 (1.0 -> 1.0.0).
# Release line behind the latest tag: refuse. Going backwards would reissue
# numbers that already have a GitHub release and a ghcr.io image.
#
# Versions stay plain MAJOR.MINOR.PATCH on every channel. The update checker
# compares them with System.Version and the release asset names embed them, so
# a -beta suffix would break both. The channel lives on the release, not in the
# number.
set -euo pipefail

if [ $# -ne 2 ]; then
  echo "usage: next-version.sh <latest-tag> <release-line>" >&2
  exit 2
fi

latest="${1#v}"
line="$(echo "$2" | tr -d '[:space:]')"

if ! [[ "$latest" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
  echo "latest tag '$1' is not MAJOR.MINOR.PATCH" >&2
  exit 1
fi
major="${BASH_REMATCH[1]}"
minor="${BASH_REMATCH[2]}"
patch="${BASH_REMATCH[3]}"

if ! [[ "$line" =~ ^([0-9]+)\.([0-9]+)$ ]]; then
  echo "release line '$2' is not MAJOR.MINOR" >&2
  exit 1
fi
line_major="${BASH_REMATCH[1]}"
line_minor="${BASH_REMATCH[2]}"

if (( line_major == major && line_minor == minor )); then
  echo "${major}.${minor}.$((patch + 1))"
elif (( line_major > major || (line_major == major && line_minor > minor) )); then
  echo "${line_major}.${line_minor}.0"
else
  echo "release line ${line} is behind the latest tag ${major}.${minor}.${patch}" >&2
  exit 1
fi
