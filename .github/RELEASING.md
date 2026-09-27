# Releasing the media server

The cadence below was decided on 2026-09-27 (decision 4 in
NoMercy-Entertainment/.github#11). Installer signing is deferred: decision 7 was
"later" on the same day (#80).

Every build starts as a nightly. A maintainer promotes one nightly to beta,
and later the same build to stable. Nothing is rebuilt on the way.

| Channel | How it moves | Default cadence |
|---------|--------------|-----------------|
| nightly | Every push to `dev` whose CI passes | Every push |
| beta    | **Promote Release**, channel `beta` | Weekly |
| stable  | **Promote Release**, channel `stable` | Monthly, plus security fixes at any time |

The version line (for example `1.0`) is in `.github/release-line`. Raise it to
start a new minor or major line. Minor and patch releases need no sign-off;
breaking changes do.

## Promote a build

1. Pick the nightly: the newest prerelease whose CI run is green.
2. Run **Actions > Promote Release** with `dry_run` on. Read every line of the
   log. The gate refuses a draft, a retracted build, a missing asset or image,
   a commit without a successful CI run, a stable that was never a beta, and a
   stable older than the current one. The rules and their tests are in
   `.github/scripts/promote-gate.sh` and `promote-gate.test.sh`.
3. Run it again with `dry_run` off. The `release-promotion` environment asks
   a reviewer to approve.
4. Stable only: `skip_beta` is for the first stable and for an urgent security
   fix. Say why in the release notes.

## Stable release checklist

The gate enforces the first item. The rest are checked by a person before
step 3, and the result goes in the release notes.

1. CI green on the exact commit, in every repo in the release. *(Gate, for
   this repo.)*
2. Compatibility matrix filled: server, web, Android, cast, player libraries,
   nomercy-tv API.
3. Upgrade test from each of the last three stables, with a real library.
4. Clean install on the five reference machines: Windows 11, Ubuntu LTS with
   the deb, Docker on Linux, macOS on Apple silicon, one NAS.
5. Outage drill with nomercy.tv blocked: LAN playback, sign-in on a known
   device, music.
6. Installers signed (Windows Authenticode, macOS notarized). *(Deferred,
   decision 7.)* Checksums are already published: every asset has a `.sha256`
   sidecar and `manifest.json` is GPG-signed.
7. Release notes a user can read, plus a rollback note naming the previous
   stable.

## Roll back

Run **Actions > Retract Release** with the bad version and the stable to go
back to, `dry_run` first. It moves the stable Docker tags back, marks the bad
release retracted and makes the rollback target GitHub's latest release again.
It never deletes a release or a version tag, so pinned installs keep working.
