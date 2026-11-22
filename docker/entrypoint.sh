#!/bin/sh
# NoMercy container entrypoint.
# Images before 4ee22ca0f ran as root, so existing volumes hold root-owned files.
# Started as root, this script hands /data to the nomercy user (only when the
# owner differs, so a normal start stays fast), then drops to nomercy and runs
# the server command. Started as another user (docker run --user), it just runs.
set -eu

# Hands the files of one folder to nomercy. A folder mounted below it (a media
# library under /data) is the user's own: it is skipped and keeps its owner.
# Two volumes can share one device, so the mount table decides, not find -xdev.
hand_over() {
    dir="$1"
    shift $#
    set -f
    IFS='
'
    for mount in $(awk -v d="$dir/" 'index($5, d) == 1 { print $5 }' /proc/self/mountinfo | sed 's/\\040/ /g'); do
        set -- "$@" -path "$mount" -prune -o
    done
    unset IFS
    set +f
    find "$dir" -xdev "$@" ! -uid "$uid" -exec chown -h nomercy:nomercy {} + 2>/dev/null || true
}

if [ "$(id -u)" = "0" ]; then
    uid="$(id -u nomercy)"
    for dir in /data /app; do
        [ -d "$dir" ] || continue
        (hand_over "$dir")
    done
    exec setpriv --reuid=nomercy --regid=nomercy --init-groups "$@"
fi

exec "$@"
