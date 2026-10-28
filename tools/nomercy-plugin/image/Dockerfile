# The marketplace build image: .NET SDK, the nomercy-plugin tool, and an offline
# NuGet feed. Run with --network none; the feed is everything a restore may use.
#
# Build from the repository root:
#   docker build -f tools/nomercy-plugin/image/Dockerfile -t nomercy-plugin-build .
#
# Only this build step has a network: it packs the SDK from this source tree and
# vendors the packages in offline-feed-seed.csproj. To let a plugin use another
# third-party package, add it to the seed and rebuild the image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .

RUN dotnet pack src/NoMercy.PluginSdk.Abstractions -c Release -o /feed \
 && dotnet pack src/NoMercy.PluginSdk.Mvc -c Release -o /feed \
 && dotnet publish tools/nomercy-plugin -c Release -o /cli

# The seed restores outside the repository so its central package management
# and analyzers do not apply. Every .nupkg it extracted joins the feed.
WORKDIR /seed
COPY tools/nomercy-plugin/image/offline-feed-seed.csproj .
RUN dotnet restore offline-feed-seed.csproj --packages /seed/packages \
      --source /feed --source https://api.nuget.org/v3/index.json \
 && find /seed/packages -name '*.nupkg' ! -iname 'nomercy.pluginsdk.*' -exec cp -n {} /feed/ \;

FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY --from=build /feed /opt/nomercy/feed
COPY --from=build /cli /opt/nomercy/cli
RUN printf '#!/bin/sh\nexec dotnet /opt/nomercy/cli/NoMercy.Plugin.Cli.dll "$@"\n' \
      > /usr/local/bin/nomercy-plugin \
 && chmod +x /usr/local/bin/nomercy-plugin

ENV NOMERCY_PLUGIN_FEED=/opt/nomercy/feed \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
