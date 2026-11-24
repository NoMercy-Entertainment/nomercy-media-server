#!/usr/bin/env bash
# Tests for changelog-commits.sh. Run: bash .github/scripts/changelog-commits.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
failures=0

# Repo shaped like the real one: releases are squashed commits on master that
# are NOT ancestors of dev, and the only tag reachable from dev is an old
# feature-branch tag.
cd "$tmp" && git init -q -b dev . && git config user.email t@example.com && git config user.name t
git config tag.gpgsign false && git config commit.gpgsign false && git config tag.forceSignAnnotated false
commit() { # <date> <subject>
  mkdir -p src && echo "$2" >> src/a.txt && git add -A
  GIT_AUTHOR_DATE="$1" GIT_COMMITTER_DATE="$1" git commit -q -m "$2"
}
commit 2026-01-01T10:00:00 "feat: ancient feature"
git tag v0.1.404
commit 2026-02-01T10:00:00 "fix: shipped in 0.1.539"
commit 2026-03-01T10:00:00 "feat: shipped in 0.1.540"
# Release commits live on master, off to the side of dev.
git checkout -q -b master dev
GIT_AUTHOR_DATE=2026-03-01T12:00:00 GIT_COMMITTER_DATE=2026-03-01T12:00:00 git commit -q --allow-empty -m "release: v0.1.540"
git tag v0.1.540
git tag v0.1.540-beta.1
git checkout -q dev
commit 2026-03-02T10:00:00 "feat: new after 0.1.540"
commit 2026-03-03T10:00:00 "chore: bump version to 0.1.541"
git update-ref refs/remotes/origin/dev dev

got="$(bash "$here/changelog-commits.sh" origin/dev 2>/dev/null)"
want="feat: new after 0.1.540"
if [ "$got" = "$want" ]; then
  echo "ok    range starts at the newest release tag (v0.1.540)"
else
  echo "FAIL  got: '$got'"
  echo "      want: '$want'"
  failures=$((failures + 1))
fi

echo "(git describe on dev still says: $(git describe --tags --abbrev=0 origin/dev))"
[ $failures -eq 0 ] || { echo "$failures failure(s)"; exit 1; }
echo "all passed"
