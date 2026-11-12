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

using System.Net;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Controllers;

[Trait("Category", "Plugins")]
public class PluginLocationOwnerOnlyTests : IClassFixture<NoMercyApiFactory>
{
    private const string Locations = "/api/v1/plugins/locations";
    private const string Files = "/api/v1/plugins/locations/placeholder-location/files";

    private readonly HttpClient _owner;
    private readonly HttpClient _member;

    public PluginLocationOwnerOnlyTests(NoMercyApiFactory factory)
    {
        _owner = factory.CreateClient().AsAuthenticated();
        _member = factory.CreateClient().AsSecondaryUser();
    }

    [Theory]
    [InlineData(Locations)]
    [InlineData(Files)]
    public async Task NonOwner_IsForbidden(string route)
    {
        HttpResponseMessage response = await _member.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Owner_ListsLocations()
    {
        HttpResponseMessage response = await _owner.GetAsync(Locations);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Owner_PassesTheGateOnFiles()
    {
        HttpResponseMessage response = await _owner.GetAsync(Files);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
