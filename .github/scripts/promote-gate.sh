#!/usr/bin/env bash
# Decides whether a release may be promoted. Prints each check and exits 1 on
# the first refusal, so promote-release.yml and its tests share one rule set.
#
#   promote-gate.sh <release.json> <ci-runs.json> <version> <channel> <skip-beta> [current-stable]
#
# <release.json>    gh release view --json isDraft,isPrerelease,body,assets
# <ci-runs.json>    CI/CD Pipeline runs for the build commit: [{"conclusion": ...}, ...]
# <version>         MAJOR.MINOR.PATCH being promoted
# <channel>         beta | stable
# <skip-beta>       true lets a stable skip beta (first stable, urgent fix)
# [current-stable]  version GitHub currently calls latest, empty when none
#
# Network reads stay in the workflow; this script only judges what it is given.
set -euo pipefail

if [ $# -lt 5 ] || [ $# -gt 6 ]; then
  echo "usage: promote-gate.sh <release.json> <ci-runs.json> <version> <channel> <skip-beta> [current-stable]" >&2
  exit 2
fi

release="$1"
runs="$2"
version="$3"
channel="$4"
skip_beta="$5"
current="${6:-}"
current="${current#v}"

refuse() {
  echo "::error::$1"
  exit 1
}

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || refuse "version must be MAJOR.MINOR.PATCH without a leading v, got '$version'"
[[ "$channel" == "beta" || "$channel" == "stable" ]] || refuse "channel must be beta or stable, got '$channel'"

[ "$(jq -r .isDraft "$release")" = "true" ] && refuse "v$version is a draft (a staged branch build), not a nightly"
jq -r '.body // ""' "$release" | grep -q 'RETRACTED' && refuse "v$version was retracted and cannot be promoted"

# What a user installs from, per platform. A release missing any of these
# would promote a broken download.
required=(
  manifest.json
  manifest.json.sig
  release-info.json
  nomercy-linux-x64
  nomercy-windows-x64.exe
  nomercy-macos-x64
  nomercy-macos-arm64
  "NoMercyMediaServer-${version}-windows-x64-setup.exe"
  "NoMercyMediaServer-${version}-macos-x64-setup.pkg"
  "nomercy_${version}_amd64.deb"
  "nomercy-${version}-1.x86_64.rpm"
  "nomercy-${version}-1-x86_64.pkg.tar.zst"
)
missing=0
for name in "${required[@]}"; do
  if jq -e --arg n "$name" '.assets | any(.name == $n)' "$release" >/dev/null; then
    echo "ok      $name"
  else
    echo "::error::v$version is missing the asset $name"
    missing=1
  fi
done
[ "$missing" -eq 0 ] || exit 1

# A nightly only exists if its pipeline's test job passed, but a rerun, a
# manual release or a later failed job can leave a release behind a run that
# did not succeed.
jq -e 'any(.conclusion == "success")' "$runs" >/dev/null \
  || refuse "no successful CI/CD Pipeline run for the commit v$version was built from. A build from red CI is never promoted."
echo "ok      CI succeeded for the build commit"

if [ "$channel" = "beta" ]; then
  [ "$(jq -r .isPrerelease "$release")" = "false" ] && refuse "v$version is already a stable release"
  echo "ok      v$version may go to beta"
  exit 0
fi

# "Update the GitHub release" (the step after this gate) rewrites the
# channel marker to "stable" and, on success, flips isPrerelease to false.
# So a promotion that reached that step and then failed later (the Docker
# tag move, the packages dispatch) leaves no "beta" marker for a retry to
# find: the release now looks, to this check, like it was never a beta.
# isPrerelease already false is that retry, not a regression, so it is
# not asked to prove it was a beta again.
was_beta="false"
jq -r '.body // ""' "$release" | grep -q '<!-- nomercy-channel: beta -->' && was_beta="true"
already_stable="false"
[ "$(jq -r .isPrerelease "$release")" = "false" ] && already_stable="true"
if [ "$was_beta" != "true" ] && [ "$already_stable" != "true" ] && [ "$skip_beta" != "true" ]; then
  refuse "v$version was never a beta. Promote it to beta first, or set skip_beta for a first stable or an urgent fix."
fi

# Stable never goes backwards: the updaters would see an older version as
# "latest" and every server would report no update.
if [ -n "$current" ] && [ "$current" != "$version" ]; then
  newest=$(printf '%s\n%s\n' "$current" "$version" | sort -V | tail -1)
  [ "$newest" = "$version" ] || refuse "v$version is older than the current stable v$current"
fi
echo "ok      v$version may go to stable (current stable: ${current:-none})"
