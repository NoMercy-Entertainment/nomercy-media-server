#!/bin/sh
# Copies one released visualizer bundle into the server repo and prints its bundle_hash.
# Usage: update-visualizer-bundle.sh <folder with visualizers-bundle.json.gz and textures/>
# The server build fetches nothing from other repos; this pinned copy is what ships.
set -eu

if [ "$#" -ne 1 ]; then
    echo "usage: $0 <bundle folder>" >&2
    exit 2
fi

source_dir="$1"
script_dir="$(cd "$(dirname "$0")" && pwd)"
target_dir="$script_dir/../src/NoMercy.Data/Visualizers/Bundle"
bundle="$source_dir/visualizers-bundle.json.gz"

if [ ! -f "$bundle" ] || [ ! -d "$source_dir/textures" ]; then
    echo "missing visualizers-bundle.json.gz or textures/ in $source_dir" >&2
    exit 1
fi

bundle_hash="$(gzip -dc "$bundle" | grep -o '"bundle_hash":"[0-9a-f]\{64\}"' | head -n 1 | cut -d '"' -f 4)"
if [ -z "$bundle_hash" ]; then
    echo "no bundle_hash found in $bundle" >&2
    exit 1
fi

mkdir -p "$target_dir/textures"
rm -f "$target_dir"/textures/*.jpg
cp "$bundle" "$target_dir/visualizers-bundle.json.gz"
cp "$source_dir"/textures/*.jpg "$target_dir/textures/"
rm -f "$target_dir"/previews/*.webp
if [ -d "$source_dir/previews" ]; then
    mkdir -p "$target_dir/previews"
    cp "$source_dir"/previews/*.webp "$target_dir/previews/"
fi
printf '%s\n' "$bundle_hash" > "$target_dir/bundle-hash.txt"

echo "bundle_hash $bundle_hash"
