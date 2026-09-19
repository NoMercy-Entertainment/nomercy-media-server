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

using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NoMercy.Api.Constraints;
using NoMercy.Api.Hubs.Filters;
using NoMercy.Api.Middleware;
using NoMercy.Api.Plugins;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.Service.Configuration.Swagger;

namespace NoMercy.Service.Configuration;

public static partial class ServiceConfiguration
{
    private static void ConfigureApi(IServiceCollection services)
    {
        ConfigureApiVersioning(services);

        // Add Controllers and JSON Options
        IMvcBuilder mvc = services
            .AddControllers(options =>
            {
                options.EnableEndpointRouting = true;
            })
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                options.SerializerSettings.DateFormatHandling = DateFormatHandling.IsoDateFormat;
                options.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
                options.SerializerSettings.Converters.Add(new StringEnumConverter());
            });

        ConfigurePluginMvc(services, mvc);

        services.Configure<HmacValidationOptions>(_ => { });
        services.Configure<RouteOptions>(options =>
        {
            options.ConstraintMap.Add("ulid", typeof(UlidRouteConstraint));
        });

        // Add Other Services
        services.AddDirectoryBrowser();
        services.AddResponseCaching();
        services.AddMvc(option => option.EnableEndpointRouting = false);
        services.AddEndpointsApiExplorer();

        services.AddHttpContextAccessor();
        // Align SignalR per-user routing with Device.OwnerUserId so per-user
        // pushes never leak across accounts.
        services.AddSingleton<IUserIdProvider, Api.Hubs.NoMercyUserIdProvider>();
        services.AddSingleton<IServedFolderRegistry, ServedFolderRegistry>();

        services
            .AddSignalR(o =>
            {
                o.EnableDetailedErrors = Config.IsDev;
                o.MaximumReceiveMessageSize = 2 * 1024 * 1024; // 2MB — realistic max is ~1MB for large playlists

                o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                o.KeepAliveInterval = TimeSpan.FromSeconds(15);

                // Add error logging filter for invalid method calls and wrong arguments
                o.AddFilter<HubErrorLoggingFilter>();
            })
            .AddNewtonsoftJsonProtocol(options =>
            {
                options.PayloadSerializerSettings = JsonHelper.Settings;
            });

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            // Subtitle payloads (Aegisub karaoke + hand-drawn signs) reach
            // 60-90 MB. Compressing them strips Content-Length from the
            // OkHttp response on the Android client (transparent gzip leaves
            // the header reflecting the compressed bytes, useless for sizing
            // the read buffer). Result: the client falls into its no-length
            // fallback path and OOMs the 256 MB-heap TV / 384 MB phone. Skip
            // compression for subtitle MIME types so clients always know the
            // real payload size up front.
            options.ExcludedMimeTypes =
            [
                "text/x-ssa",
                "text/x-ass",
                "application/x-subrip",
                "text/vtt",
            ];
        });

        SwaggerConfiguration.AddSwagger(services);
    }

    private static void ConfigureApiVersioning(IServiceCollection services)
    {
        services
            .AddApiVersioning(config =>
            {
                config.ReportApiVersions = true;
                config.AssumeDefaultVersionWhenUnspecified = true;
                config.DefaultApiVersion = new(1, 0);
                config.UnsupportedApiVersionStatusCode = 418;
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
                options.DefaultApiVersion = new(1, 0);
            });
    }
}
