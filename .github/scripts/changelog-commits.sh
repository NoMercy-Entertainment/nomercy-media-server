#!/usr/bin/env bash
# Prints the subjects of the commits that belong in the next release notes.
#
#   changelog-commits.sh <ref>      (for example origin/dev)
#
# "Since the last release" means since the newest plain vMAJOR.MINOR.PATCH tag
# in the whole repo. Do not use `git describe` here: release tags sit on master
# commits that are not ancestors of dev, so describe resolves to an old tag
# reachable from dev (v0.1.404) and the notes list ~136 releases.
#
# Same lookup as version-and-release-management.yml ("Get latest tag").
#
# A release commit on master is a squash of dev, so the tag is usually not an
# ancestor of <ref>. Then the range starts at the tag's commit date. When the
# tag is an ancestor, the exact range tag..<ref> is used.
#
# Only src/ commits count, and version bump commits are dropped.
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: changelog-commits.sh <ref>" >&2
  exit 2
fi
ref="$1"

latest="$(git tag -l 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sed 's/^v//' | sort -t. -k1,1n -k2,2n -k3,3n | tail -1 || true)"

if [ -z "$latest" ]; then
  # No release tag at all: fall back to the first release line.
  range=("v0.1.201..$ref")
  since=()
elif git merge-base --is-ancestor "v$latest" "$ref" 2>/dev/null; then
  range=("v$latest..$ref")
  since=()
else
  range=("$ref")
  since=("--since=$(git log -1 --format=%cI "v$latest")")
fi

git log "${since[@]+"${since[@]}"}" "${range[@]}" --pretty=format:"%s" -- src/ | grep -v "^chore: bump version" || true
