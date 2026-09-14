// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using NoMercy.Api.Hubs;
using NoMercy.Api.Services.Music;
using NoMercy.Data.Services.Music;
using NoMercy.Setup.Cast;

namespace NoMercy.Service.Extensions;

public static class MusicHubServiceExtensions
{
    public static IServiceCollection AddMusicHubServices(this IServiceCollection services)
    {
        // Singletons - shared state across requests
        services.AddSingleton<MusicPlayerStateManager>();
        services.AddSingleton<MusicActiveDeviceRegistry>();
        services.AddSingleton<MusicPlaybackService>();
        services.AddSingleton<MusicPlaybackCommandHandler>();
        // Single-flight lyric fetch coalescing across concurrent device requests.
        services.AddSingleton<LyricsResolver>();
        services.AddSingleton<CastPanelWakeLauncher>();

        // Scoped - one instance per request
        services.AddScoped<MusicPlaylistManager>();
        services.AddScoped<MusicHub>();

        return services;
    }
}
