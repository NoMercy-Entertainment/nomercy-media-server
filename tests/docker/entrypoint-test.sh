#!/usr/bin/env bash
# Proves docker/entrypoint.sh: a container started as root on a volume written by the
# old root image hands the volume to uid 1000, then runs the server command as nomercy.
# Uses a throwaway image and volume, publishes no port, and removes both.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
tag="nm-entrypoint-test:$$"
vol="nm-entrypoint-test-vol-$$"
ctx="$(mktemp -d)"
cleanup() { docker volume rm -f "$vol" >/dev/null 2>&1 || true; docker rmi -f "$tag" >/dev/null 2>&1 || true; rm -rf "$ctx"; }
trap cleanup EXIT

cp "$repo/docker/entrypoint.sh" "$ctx/entrypoint.sh"
cat > "$ctx/Dockerfile" <<'DF'
FROM debian:bookworm-slim
RUN useradd -m -o -u 1000 nomercy && mkdir -p /data && chown nomercy:nomercy /data
COPY entrypoint.sh /usr/local/bin/nomercy-entrypoint.sh
RUN chmod +x /usr/local/bin/nomercy-entrypoint.sh
ENV HOME=/data
ENTRYPOINT ["/usr/local/bin/nomercy-entrypoint.sh"]
DF
docker build -q -t "$tag" "$ctx" >/dev/null

# Old install: data written by the old image, owned by root.
docker run --rm --entrypoint sh -v "$vol:/data" "$tag" -c \
  'mkdir -p /data/.local/share/NoMercy && echo x > /data/.local/share/NoMercy/db && chown -R 0:0 /data'

out="$(docker run --rm -v "$vol:/data" "$tag" sh -c \
  'echo "who=$(id -un)"; echo hello > /data/.local/share/NoMercy/new && echo "write=ok"; echo "owner=$(stat -c %u /data/.local/share/NoMercy/db)"')"
echo "$out"
grep -x 'who=nomercy' <<<"$out" >/dev/null || { echo "FAIL: server command did not run as nomercy"; exit 1; }
grep -x 'write=ok' <<<"$out" >/dev/null    || { echo "FAIL: nomercy cannot write to the old root-owned volume"; exit 1; }
grep -x 'owner=1000' <<<"$out" >/dev/null  || { echo "FAIL: old files were not handed to uid 1000"; exit 1; }

# A user who already runs with --user keeps working without root.
out2="$(docker run --rm --user 1000:1000 -v "$vol:/data" "$tag" sh -c 'echo "who=$(id -u)"')"
grep -x 'who=1000' <<<"$out2" >/dev/null || { echo "FAIL: --user 1000 start broke"; exit 1; }
# A media folder mounted below /data is the user's own: its files keep their owner.
media="nm-entrypoint-test-media-$$"
trap 'docker volume rm -f "$media" >/dev/null 2>&1 || true; cleanup' EXIT
docker run --rm --entrypoint sh -v "$vol:/data" -v "$media:/data/media" "$tag" -c \
  'echo m > /data/media/movie.mkv && chown 0:0 /data/media/movie.mkv && echo y > /data/old && chown 0:0 /data/old'
out3="$(docker run --rm -v "$vol:/data" -v "$media:/data/media" "$tag" sh -c \
  'echo "old=$(stat -c %u /data/old)"; echo "media=$(stat -c %u /data/media/movie.mkv)"')"
echo "$out3"
grep -x 'old=1000' <<<"$out3" >/dev/null || { echo "FAIL: old file on the data volume was not handed over"; exit 1; }
grep -x 'media=0' <<<"$out3" >/dev/null  || { echo "FAIL: a file on a volume mounted below /data changed owner"; exit 1; }

echo "PASS: entrypoint migrates a root-owned volume and drops to nomercy"
