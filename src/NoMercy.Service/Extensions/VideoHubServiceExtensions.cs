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
using NoMercy.Api.Services.Video;

namespace NoMercy.Service.Extensions;

public static class VideoHubServiceExtensions
{
    public static IServiceCollection AddVideoHubServices(this IServiceCollection services)
    {
        // Singletons - shared state across requests
        services.AddSingleton<VideoPlayerStateManager>();
        services.AddSingleton<VideoPlaybackService>();
        services.AddSingleton<VideoPlaybackCommandHandler>();

        // Scoped - one instance per request
        services.AddScoped<VideoPlaylistManager>();
        services.AddScoped<VideoHub>();

        return services;
    }
}
