#!/bin/sh
# NoMercy container entrypoint.
# Images before 4ee22ca0f ran as root, so existing volumes hold root-owned files.
# Started as root, this script hands /data to the nomercy user (only when the
# owner differs, so a normal start stays fast), then drops to nomercy and runs
# the server command. Started as another user (docker run --user), it just runs.
set -eu

if [ "$(id -u)" = "0" ]; then
    uid="$(id -u nomercy)"
    for dir in /data /app; do
        [ -d "$dir" ] || continue
        if [ "$(stat -c %u "$dir")" != "$uid" ] || \
           [ -n "$(find "$dir" -xdev ! -uid "$uid" -print -quit 2>/dev/null)" ]; then
            chown -R nomercy:nomercy "$dir" 2>/dev/null || true
        fi
    done
    exec setpriv --reuid=nomercy --regid=nomercy --init-groups "$@"
fi

exec "$@"
