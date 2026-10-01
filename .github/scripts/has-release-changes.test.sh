#!/usr/bin/env bash
# Tests for has-release-changes.sh. Run: bash .github/scripts/has-release-changes.test.sh
# Builds a throwaway repo shaped like this one, then checks which diffs count as a release.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
failures=0
repo="$(mktemp -d)"
trap 'rm -rf "${repo}"' EXIT

g() { git -C "${repo}" -c core.autocrlf=false -c user.name=test -c user.email=test@example.invalid "$@"; }
put() { mkdir -p "$(dirname "${repo}/$1")"; printf '%s\n' "$2" >> "${repo}/$1"; }

g init -q
put .github/workflows/build-packages.yml "      - uses: ./.github/actions/build-rpm-packages"
put .github/workflows/build-executables.yml "      - uses: ./.github/actions/build-dotnet-project"
put .github/actions/build-rpm-packages/action.yml "      uses: ./.github/actions/build-linux-installer-payload"
put .github/actions/build-linux-installer-payload/action.yml "name: payload"
put .github/actions/build-dotnet-project/action.yml "name: dotnet"
put .github/actions/unrelated/action.yml "name: unrelated"
put src/Program.cs "class Program {}"
put assets/linux/nomercy.desktop "[Desktop Entry]"
put packaging/macos/build-pkg.sh "echo pkg"
put docker/Dockerfile.cpu "FROM scratch"
put Directory.Build.props "<Project />"
put Directory.Packages.props "<Project />"
put docs/README.md "docs"
g add -A
g commit -q -m base
base="$(g rev-parse HEAD)"

run() { (cd "${repo}" && bash "${here}/has-release-changes.sh" "${base}" HEAD 2> /dev/null); }

change() {
  g checkout -q "${base}"
  put "$1" "change"
  g add -A
  g commit -q -m "change $1"
}

expect() {
  local want="$1" path="$2" got
  change "${path}"
  got="$(run)"
  if [ "${got}" = "${want}" ]; then
    echo "ok    ${path} -> ${got}"
  else
    echo "FAIL  ${path} -> '${got}', want '${want}'"
    failures=$((failures + 1))
  fi
}

expect true src/Program.cs
expect true .github/actions/build-rpm-packages/templates/nomercy.spec
expect true .github/actions/build-linux-installer-payload/action.yml
expect true .github/actions/build-dotnet-project/action.yml
expect true assets/linux/nomercy.desktop
expect true packaging/macos/build-pkg.sh
expect true docker/Dockerfile.cpu
expect true Directory.Packages.props
expect false docs/README.md
expect false .github/actions/unrelated/action.yml

# A renamed input must stop the check, not drop out of the list.
g checkout -q "${base}"
g mv packaging packaging-renamed
g commit -q -m "rename packaging"
if run > /dev/null; then
  echo "FAIL  renamed packaging/ was accepted, want refused"
  failures=$((failures + 1))
else
  echo "ok    renamed packaging/ refused"
fi

if (cd "${repo}" && bash "${here}/has-release-changes.sh" no-such-tag HEAD > /dev/null 2>&1); then
  echo "FAIL  unknown since ref was accepted, want refused"
  failures=$((failures + 1))
else
  echo "ok    unknown since ref refused"
fi

if [ "${failures}" -gt 0 ]; then
  echo "${failures} failure(s)"
  exit 1
fi
echo "all passed"
