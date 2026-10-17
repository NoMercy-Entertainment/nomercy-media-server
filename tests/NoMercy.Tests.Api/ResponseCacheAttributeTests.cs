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
using NoMercy.Api.Controllers.V1;
using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using NoMercy.Api.Controllers.V1.Media;
using Xunit;
using MediaLibrariesController = NoMercy.Api.Controllers.V1.Media.LibrariesController;

namespace NoMercy.Tests.Api;

[Trait("Category", "Unit")]
public class ResponseCacheAttributeTests
{
    [Theory]
    [InlineData([typeof(ConfigurationController), "Languages", 3600])]
    [InlineData([typeof(ConfigurationController), "Countries", 3600])]
    [InlineData([typeof(ServerController), "ServerPaths", 3600])]
    [InlineData([typeof(SetupController), "Status", 30])]
    public void CacheableEndpoint_HasResponseCacheAttribute_WithCorrectDuration(
        Type controllerType,
        string methodName,
        int expectedDuration
    )
    {
        MethodInfo? method = controllerType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public
        );
        Assert.NotNull(method);

        ResponseCacheAttribute? attr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(expectedDuration, attr.Duration);
        Assert.False(
            attr.NoStore,
            $"{controllerType.Name}.{methodName} should not have NoStore=true"
        );
    }

    [Theory]
    [InlineData([typeof(UserDataController), "ContinueWatching"])]
    [InlineData([typeof(HomeController), "Home"])]
    [InlineData([typeof(SearchController), "SearchMusic"])]
    [InlineData([typeof(SearchController), "SearchVideo"])]
    [InlineData([typeof(ServerController), "Resources"])]
    [InlineData([typeof(ServerController), "ServerInfo"])]
    [InlineData([typeof(SetupController), "ServerInfo"])]
    // Library-backed media: a scan changes them, and RefreshLibrary refetches them.
    [InlineData([typeof(GenresController), nameof(GenresController.Genres)])]
    [InlineData([typeof(GenresController), nameof(GenresController.Genre)])]
    [InlineData([typeof(PeopleController), "Index"])]
    [InlineData([typeof(PeopleController), "Show"])]
    [InlineData([typeof(CollectionsController), "Collections"])]
    [InlineData([typeof(CollectionsController), "Collection"])]
    [InlineData([typeof(MediaLibrariesController), "Libraries"])]
    [InlineData([typeof(MoviesController), "Movie"])]
    [InlineData([typeof(TvShowsController), "Tv"])]
    public void RealTimeEndpoint_HasResponseCacheNoStore(Type controllerType, string methodName)
    {
        MethodInfo? method = controllerType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public
        );
        Assert.NotNull(method);

        ResponseCacheAttribute? attr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(attr);
        Assert.True(attr.NoStore, $"{controllerType.Name}.{methodName} should have NoStore=true");
    }
}
