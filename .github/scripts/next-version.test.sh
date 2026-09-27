#!/usr/bin/env bash
# Tests for next-version.sh. Run: bash .github/scripts/next-version.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
failures=0

expect() {
  local want="$1"; shift
  local got
  got="$(bash "$here/next-version.sh" "$@" 2>/dev/null)"
  if [ "$got" = "$want" ]; then
    echo "ok    $* -> $got"
  else
    echo "FAIL  $* -> '$got', want '$want'"
    failures=$((failures + 1))
  fi
}

expect_refused() {
  if bash "$here/next-version.sh" "$@" >/dev/null 2>&1; then
    echo "FAIL  $* was accepted, want refused"
    failures=$((failures + 1))
  else
    echo "ok    $* refused"
  fi
}

expect 0.1.533 v0.1.532 0.1
expect 0.1.533 0.1.532 0.1
expect 1.0.0 v0.1.532 1.0
expect 1.1.0 v1.0.14 1.1
expect 2.0.0 v1.9.3 2.0
expect 1.0.15 v1.0.14 "1.0
"
expect_refused v1.0.0 0.1
expect_refused v1.2.0 1.1
expect_refused v0.1.532-nightly 0.1
expect_refused v0.1.532 1
expect_refused v0.1.532

if [ "$failures" -gt 0 ]; then
  echo "$failures failure(s)"
  exit 1
fi
echo "all passed"
