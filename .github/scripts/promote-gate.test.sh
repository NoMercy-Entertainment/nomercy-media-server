#!/usr/bin/env bash
# Tests for promote-gate.sh. Run: bash .github/scripts/promote-gate.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
failures=0

v=1.0.14
assets=$(jq -nc --arg v "$v" '[
  "manifest.json", "manifest.json.sig", "release-info.json",
  "nomercy-linux-x64", "nomercy-windows-x64.exe", "nomercy-macos-x64", "nomercy-macos-arm64",
  "NoMercyMediaServer-\($v)-windows-x64-setup.exe", "NoMercyMediaServer-\($v)-macos-x64-setup.pkg",
  "nomercy_\($v)_amd64.deb", "nomercy-\($v)-1.x86_64.rpm", "nomercy-\($v)-1-x86_64.pkg.tar.zst"
] | map({name: .})')

# release <file> <jq filter applied to a complete nightly>
release() {
  jq -n --argjson a "$assets" \
    '{isDraft: false, isPrerelease: true, body: "## Install\n", assets: $a}' | jq "$2" > "$tmp/$1"
}

echo '[{"conclusion": "failure"}, {"conclusion": "success"}]' > "$tmp/green.json"
echo '[{"conclusion": "failure"}, {"conclusion": null}]' > "$tmp/red.json"
echo '[]' > "$tmp/none.json"

release nightly.json '.'
release beta.json '.body = "<!-- nomercy-channel: beta -->\n" + .body'
release stable.json '.isPrerelease = false | .body = "<!-- nomercy-channel: stable -->\n" + .body'
release draft.json '.isDraft = true'
release retracted.json '.body = "> [!CAUTION]\n> **RETRACTED v1.0.14.** broke playback\n" + .body'
release no-deb.json '.assets |= map(select(.name != "nomercy_1.0.14_amd64.deb"))'
release null-body.json '.body = null'
# A stable promotion whose "Update the GitHub release" step succeeded (marker
# rewritten to stable, isPrerelease flipped) before a later step (Docker move,
# packages dispatch) failed. The beta marker this gate used to require is
# already gone by the time someone retries.
release retry-stable.json '.isPrerelease = false | .body = "<!-- nomercy-channel: stable -->\n" + .body'

run() {
  bash "$here/promote-gate.sh" "$@" >"$tmp/out" 2>&1
}

allowed() {
  local label="$1"; shift
  if run "$@"; then
    echo "ok    allowed: $label"
  else
    echo "FAIL  refused, want allowed: $label"
    sed 's/^/        /' "$tmp/out"
    failures=$((failures + 1))
  fi
}

refused() {
  local label="$1" reason="$2"; shift 2
  if run "$@"; then
    echo "FAIL  allowed, want refused: $label"
    failures=$((failures + 1))
  elif grep -q -- "$reason" "$tmp/out"; then
    echo "ok    refused: $label"
  else
    echo "FAIL  refused for the wrong reason: $label (want '$reason')"
    sed 's/^/        /' "$tmp/out"
    failures=$((failures + 1))
  fi
}

allowed "nightly to beta" "$tmp/nightly.json" "$tmp/green.json" $v beta false
allowed "beta to stable" "$tmp/beta.json" "$tmp/green.json" $v stable false 1.0.2
allowed "nightly to stable with skip_beta (first stable)" "$tmp/nightly.json" "$tmp/green.json" $v stable true
allowed "stable rerun of the current stable" "$tmp/beta.json" "$tmp/green.json" $v stable false v1.0.14
allowed "numeric, not text, version order" "$tmp/beta.json" "$tmp/green.json" $v stable false 1.0.9
allowed "release without notes to beta" "$tmp/null-body.json" "$tmp/green.json" $v beta false
allowed "retry a stable promotion that already flipped" "$tmp/retry-stable.json" "$tmp/green.json" $v stable false 1.0.14

refused "draft" "is a draft" "$tmp/draft.json" "$tmp/green.json" $v beta false
refused "retracted, to beta" "was retracted" "$tmp/retracted.json" "$tmp/green.json" $v beta false
refused "retracted, to stable with skip_beta" "was retracted" "$tmp/retracted.json" "$tmp/green.json" $v stable true
refused "missing asset" "missing the asset nomercy_1.0.14_amd64.deb" "$tmp/no-deb.json" "$tmp/green.json" $v beta false
refused "no successful CI run" "no successful CI/CD Pipeline run" "$tmp/nightly.json" "$tmp/red.json" $v beta false
refused "no CI run at all" "no successful CI/CD Pipeline run" "$tmp/nightly.json" "$tmp/none.json" $v stable true
refused "never beta, to stable" "was never a beta" "$tmp/nightly.json" "$tmp/green.json" $v stable false
refused "never beta, release without notes" "was never a beta" "$tmp/null-body.json" "$tmp/green.json" $v stable false
refused "stable going backwards" "older than the current stable v1.1.0" "$tmp/beta.json" "$tmp/green.json" $v stable false v1.1.0
refused "beta of a stable release" "already a stable release" "$tmp/stable.json" "$tmp/green.json" $v beta false
refused "version with a leading v" "MAJOR.MINOR.PATCH" "$tmp/nightly.json" "$tmp/green.json" v1.0.14 beta false
refused "unknown channel" "channel must be beta or stable" "$tmp/nightly.json" "$tmp/green.json" $v nightly false

if [ "$failures" -gt 0 ]; then
  echo "$failures failure(s)"
  exit 1
fi
echo "all passed"
