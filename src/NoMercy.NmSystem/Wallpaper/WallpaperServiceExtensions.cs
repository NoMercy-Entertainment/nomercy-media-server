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

using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace NoMercy.NmSystem.Wallpaper;

public static class WallpaperServiceExtensions
{
    public static IServiceCollection AddWallpaperService(this IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            services.AddSingleton<IWallpaperService, WindowsWallpaperService>();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IWallpaperCommandRunner, WallpaperCommandRunner>();
            services.AddSingleton<IWallpaperService, MacWallpaperService>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            bool hasDisplay =
                Environment.GetEnvironmentVariable("DISPLAY") is not null
                || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null;

            if (hasDisplay)
                services.AddSingleton<IWallpaperService, LinuxWallpaperService>();
            else
                services.AddSingleton<IWallpaperService, NullWallpaperService>();
        }
        else
        {
            services.AddSingleton<IWallpaperService, NullWallpaperService>();
        }

        return services;
    }
}
