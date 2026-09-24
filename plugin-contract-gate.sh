#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
npm run typecheck
npm run generate
# CSharpier is a local tool of the media server, so it runs from that repo root.
(cd ../nomercy-media-server \
  && dotnet csharpier format src/NoMercy.Plugins.Abstractions/Generated)
npm run check
npm run check:docs
npm run test
