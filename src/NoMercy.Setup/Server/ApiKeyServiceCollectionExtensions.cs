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

using Microsoft.Extensions.DependencyInjection;

namespace NoMercy.Setup.Server;

public static class ApiKeyServiceCollectionExtensions
{
    /// <summary>
    /// The API key store and loader of one host. The server builds a new host after
    /// setup and after a port conflict, so everything here must hold for a rebuilt host
    /// as much as for the first one.
    /// </summary>
    public static IServiceCollection AddApiKeys(this IServiceCollection services)
    {
        services.AddSingleton<IApiKeyStore>(_ => ApiKeyStore.ForHost());
        services.AddSingleton<IApiKeyLoader, ApiKeyLoader>();
        services.AddHostedService<ApiKeyStartupService>();
        return services;
    }
}
