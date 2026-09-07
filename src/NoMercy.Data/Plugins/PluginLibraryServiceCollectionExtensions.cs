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
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Gives the plugin platform the real library, replacing the null objects it
/// registers by default.
/// <para>
/// It is a separate call in a separate project on purpose. <c>NoMercy.Plugins</c>
/// must not reference the database — the moment it does, the EF model is one
/// step from becoming plugin ABI, which is the thing the query contract was
/// built to prevent. So the platform declares what it needs, this supplies it,
/// and the composition root is where the two meet.
/// </para>
/// <para>Call after <c>AddPluginSystem</c>; it overrides that method's defaults.</para>
/// </summary>
public static class PluginLibraryServiceCollectionExtensions
{
    public static IServiceCollection AddPluginLibraryAccess(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPluginLibraryQuery, PluginLibraryQuery>();
        services.AddSingleton<IPluginMusicQuery, PluginMusicQuery>();
        services.AddSingleton<IPluginLibraryWriterFactory, PluginLibraryWriterFactory>();

        // The three facades that retire the reflection. A plugin reached the
        // server's own job and storage types by name because there was nothing
        // else to reach, and that construction broke silently four times in
        // three days - each break a plugin that simply never encoded anything
        // while all of its tests stayed green.
        services.AddSingleton<IPluginEncoder, PluginEncoder>();
        services.AddSingleton<IPluginJobs, PluginJobs>();
        services.AddSingleton<IPluginStorage, PluginStorage>();

        return services;
    }
}
