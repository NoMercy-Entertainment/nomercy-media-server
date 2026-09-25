#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
npm run typecheck
npm run generate
# CSharpier is a local tool of the media server, so it runs from that repo root
# — two levels up now that this tool lives in-repo under tools/plugin-contract.
(cd ../.. \
  && dotnet csharpier format src/NoMercy.PluginSdk.Abstractions/Generated)
npm run check
npm run check:docs
npm run test
