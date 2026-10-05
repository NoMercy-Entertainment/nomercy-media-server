#!/usr/bin/env bash
# Prints true when the commits since <since> change anything a release ships: the server source, a local
# action the build workflows call, or a file those actions read. A packaging fix or a dependency bump alone
# must release too, or its users never get it.
# The actions come from the build workflows, followed into each action they call, so a new action counts
# without an edit here. The other inputs are listed below; the check fails when one of them is gone, so a
# rename cannot silently drop it from the list.
# Usage (from the repo root): has-release-changes.sh <since> [head]
set -euo pipefail
# On Windows Git Bash this stops the rewrite of "branch/name:path" into a Windows path; Linux ignores it.
export MSYS_NO_PATHCONV=1

since="${1:?usage: has-release-changes.sh <since> [head]}"
head="${2:-HEAD}"

WORKFLOWS=(build-packages.yml build-executables.yml)
# Read by the build actions: desktop files and icons (deb, rpm, arch, macOS), the macOS pkg script, the
# Docker images, the injected version, and the central package versions.
INPUTS=(src/ assets/ packaging/ docker/ Directory.Build.props Directory.Packages.props)

# Local actions a file calls, read at <head> so the list matches the commit being released.
actions_in() {
  git show "${head}:$1" 2> /dev/null | sed -n 's#.*uses:[[:space:]]*\./\.github/actions/\([A-Za-z0-9._-]*\).*#\1#p'
}

for ref in "${since}" "${head}"; do
  if ! git rev-parse --verify --quiet "${ref}^{commit}" > /dev/null; then
    echo "${ref} is not a commit" >&2
    exit 2
  fi
done
for input in "${INPUTS[@]}"; do
  if ! git cat-file -e "${head}:${input%/}" 2> /dev/null; then
    echo "release input ${input} does not exist at ${head}: update INPUTS in $0" >&2
    exit 2
  fi
done

paths=("${INPUTS[@]}")
queue=()
for workflow in "${WORKFLOWS[@]}"; do
  mapfile -t found < <(actions_in ".github/workflows/${workflow}")
  if [ "${#found[@]}" -eq 0 ]; then
    echo "no local actions found in .github/workflows/${workflow} at ${head}" >&2
    exit 2
  fi
  queue+=("${found[@]}")
done
seen=" "
while [ "${#queue[@]}" -gt 0 ]; do
  action="${queue[0]}"
  queue=("${queue[@]:1}")
  case "${seen}" in *" ${action} "*) continue ;; esac
  seen="${seen}${action} "
  paths+=(".github/actions/${action}/")
  mapfile -t nested < <(actions_in ".github/actions/${action}/action.yml")
  queue+=("${nested[@]}")
done

echo "release paths: ${paths[*]}" >&2
if git diff --quiet "${since}" "${head}" -- "${paths[@]}"; then
  echo false
else
  echo true
fi
