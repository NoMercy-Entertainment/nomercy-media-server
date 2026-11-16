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
using NoMercy.Database;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// The cast proxy finds a TV by its client device id or by the row id the device hub
/// lists it under, and only for the TV's owner. Each seeded TV has no LAN address, so a
/// TV that is found answers 503 ("no known LAN address") without any forward, and a TV
/// that is not found, or not the caller's, answers 404.
/// </summary>
[Trait("Category", "Integration")]
public class CastProxyDeviceLookupTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public CastProxyDeviceLookupTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    private static string Url(string deviceId) => $"/api/v1/cast/{deviceId}/v1/session";

    private static async Task<(Ulid RowId, string DeviceId)> SeedDeviceAsync(
        Guid owner,
        string type = "tv"
    )
    {
        string deviceId = "cast-proxy-" + Guid.NewGuid().ToString("N");
        Ulid rowId = Ulid.NewUlid();
        await using MediaContext db = new();
        db.Devices.Add(
            new()
            {
                Id = rowId,
                DeviceId = deviceId,
                Type = type,
                Name = "Placeholder TV",
                OwnerUserId = owner,
                LanIp = null,
            }
        );
        await db.SaveChangesAsync();
        return (rowId, deviceId);
    }

    [Fact]
    public async Task OwnersTv_NamedByItsClientDeviceId_IsFound()
    {
        (_, string deviceId) = await SeedDeviceAsync(TestAuthHandler.DefaultUserId);

        HttpResponseMessage response = await _factory
            .CreateClient()
            .AsAuthenticated()
            .GetAsync(Url(deviceId));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task OwnersTv_NamedByItsRowId_IsFound()
    {
        (Ulid rowId, _) = await SeedDeviceAsync(TestAuthHandler.DefaultUserId);

        HttpResponseMessage response = await _factory
            .CreateClient()
            .AsAuthenticated()
            .GetAsync(Url(rowId.ToString()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task AnotherUsersTv_IsNotFound_ByClientDeviceIdOrRowId()
    {
        (Ulid rowId, string deviceId) = await SeedDeviceAsync(TestAuthHandler.DefaultUserId);
        HttpClient secondary = _factory.CreateClient().AsSecondaryUser();

        HttpResponseMessage byClient = await secondary.GetAsync(Url(deviceId));
        HttpResponseMessage byRow = await secondary.GetAsync(Url(rowId.ToString()));

        Assert.Equal(HttpStatusCode.NotFound, byClient.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byRow.StatusCode);
    }

    [Fact]
    public async Task OwnersDeviceThatIsNotATv_IsNotFound_ByClientDeviceIdOrRowId()
    {
        (Ulid rowId, string deviceId) = await SeedDeviceAsync(
            TestAuthHandler.DefaultUserId,
            "phone"
        );
        HttpClient owner = _factory.CreateClient().AsAuthenticated();

        HttpResponseMessage byClient = await owner.GetAsync(Url(deviceId));
        HttpResponseMessage byRow = await owner.GetAsync(Url(rowId.ToString()));

        Assert.Equal(HttpStatusCode.NotFound, byClient.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byRow.StatusCode);
    }

    [Fact]
    public async Task UnknownClientDeviceId_IsNotFound()
    {
        HttpResponseMessage response = await _factory
            .CreateClient()
            .AsAuthenticated()
            .GetAsync(Url("cast-proxy-unknown"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownWellFormedRowId_IsNotFound()
    {
        HttpResponseMessage response = await _factory
            .CreateClient()
            .AsAuthenticated()
            .GetAsync(Url(Ulid.NewUlid().ToString()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
