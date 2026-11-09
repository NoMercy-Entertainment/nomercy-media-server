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

using NoMercy.NmSystem.Information;

namespace NoMercy.Service.Configuration;

public static partial class ServiceConfiguration
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Register IServerConfiguration FIRST: ConfigureHttpClients eagerly builds a
        // provider and resolves it, so it must exist before any sub-config runs. The
        // production Program.cs path registers it early too; this keeps the Startup
        // path (used by the API test host) consistent.
        services.Configure<ServerConfiguration>(configuration.GetSection("Server"));
        services.AddSingleton<IServerConfiguration, ServerConfigurationWrapper>();

        ConfigureKestrel(services);
        ConfigureHttpClients(services);
        ConfigureCoreServices(services, configuration);
        ConfigureLogging(services);
        ConfigureAuth(services);
        ConfigureApi(services);
        ConfigureCors(services);
        ConfigureCronJobs(services);
    }

    private static void ConfigureKestrel(IServiceCollection services) { }

    private static void ConfigureLogging(IServiceCollection services)
    {
        services.AddLogging(logging =>
        {
            // Logging filters are handled by CustomLogger's message filtering
            // since it replaces ILogger<T> and bypasses the built-in filter pipeline
        });
    }

    // Without this a browser repeats the OPTIONS round trip for every media URL it fetches.
    private static readonly TimeSpan PreflightMaxAge = TimeSpan.FromHours(24);

    internal static void ConfigureCors(IServiceCollection services)
    {
        // Configure CORS
        services.AddCors(options =>
        {
            options.AddPolicy(
                "AllowNoMercyOrigins",
                builder =>
                {
                    // Dev boxes hit this from whatever host/port a dev server picks
                    // that day — Vite alone has cycled through 5501/5502/5503 across
                    // sessions, plus any LAN device on the network during real device
                    // testing. A fixed origin list means every new port/host silently
                    // 403s (reads as "the app has no data", not "CORS blocked it") and
                    // needs a hand-edit here to fix. Dev is never internet-facing, so
                    // there's no origin to actually restrict — accept all of them.
                    if (Config.IsDev)
                    {
                        builder
                            .SetPreflightMaxAge(PreflightMaxAge)
                            .SetIsOriginAllowed(_ => true)
                            .AllowAnyMethod()
                            .AllowCredentials()
                            .AllowAnyHeader();
                        return;
                    }

                    builder
                        .SetPreflightMaxAge(PreflightMaxAge)
                        .WithOrigins(
                            "https://nomercy.tv",
                            "https://*.nomercy.tv",
                            "https://cast.nomercy.tv",
                            "https://hlsjs.video-dev.org",
                            "http://localhost:7625"
                        )
                        .AllowAnyMethod()
                        .AllowCredentials()
                        .SetIsOriginAllowedToAllowWildcardSubdomains()
                        .AllowAnyHeader();
                }
            );
        });
    }

    private static Ulid? TryGetDeviceId(HttpContext httpContext)
    {
        string? raw = httpContext.Request.Query["client_id"].FirstOrDefault();
        if (string.IsNullOrEmpty(raw))
            return null;

        return Ulid.TryParse(raw, out Ulid id) ? id : null;
    }
}
