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

using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.Controllers.V1.Media;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Media responses are per user and change when a scan lands. The client refetches
/// on a RefreshLibrary push; a max-age header lets the browser answer that refetch
/// from its own cache, so a rescan showed no seasons and no Watch button.
/// </summary>
public sealed class MediaResponseCacheTests
{
    private static IEnumerable<Type> MediaControllers() =>
        typeof(TvShowsController).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(TvShowsController).Namespace
                        && typeof(ControllerBase).IsAssignableFrom(t));

    private static IEnumerable<(string Name, ResponseCacheAttribute Cache)> CachedActions() =>
        from controller in MediaControllers()
        from action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        let cache = action.GetCustomAttribute<ResponseCacheAttribute>()
        where cache is not null
        select ($"{controller.Name}.{action.Name}", cache);

    public static TheoryData<string> CachedMediaActions()
    {
        TheoryData<string> data = new();
        foreach (string name in CachedActions().Select(a => a.Name).Distinct())
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CachedMediaActions))]
    public void MediaActionIsNeverStoredByTheBrowser(string action)
    {
        foreach ((string _, ResponseCacheAttribute cache) in CachedActions().Where(a => a.Name == action))
            Assert.True(cache.NoStore, $"{action} sends max-age={cache.Duration}");
    }
}
