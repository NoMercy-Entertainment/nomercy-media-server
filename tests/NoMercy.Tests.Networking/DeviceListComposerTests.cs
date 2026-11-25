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

using Moq;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Devices;
using NoMercy.Networking.Discovery;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT (Stoney, verbatim): "the tv has chromecast and should ALWAYS
/// be listed as a chromecast device. the server should correctly list every
/// chromecast device on the network and then apply a filter with the devices
/// listed as real nomercy clients." DeviceListComposer.Compose is the merge
/// point both DeviceHub.GetDevices() and DeviceBusRegistry.BroadcastChange()
/// delegate to, so proving it here proves both call sites: a registered
/// Devices row is listed once with IsRegisteredClient=true; a
/// `_googlecast._tcp` hit whose IP is NOT any registered row's LanIp is
/// listed too, IsRegisteredClient=false, with a real-shaped (never null)
/// placeholder DeviceId/Fingerprint so old, unaware clients don't choke on a
/// field they've always treated as non-null.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DeviceListComposerTests
{
    private static Device Registered(string lanIp = "192.168.1.10") =>
        new()
        {
            Id = Ulid.NewUlid(),
            DeviceId = $"dev-{Guid.NewGuid()}",
            Fingerprint = $"fp-{Guid.NewGuid()}",
            Name = "Living Room TV",
            Type = "tv",
            LanIp = lanIp,
        };

    private static Mock<ICastMdnsRegistry> Registry(params CastMdnsHit[] hits)
    {
        Mock<ICastMdnsRegistry> mock = new();
        mock.Setup(r => r.GetSeen()).Returns(hits);
        mock.Setup(r => r.IsReachable(It.IsAny<string?>()))
            .Returns((string? ip) => hits.Any(h => h.Ip == ip));
        return mock;
    }

    [Fact]
    public void Compose_NoBusSocketButOnMusicHub_RowIsOnline()
    {
        Device receiver = Registered();
        Device decoy = Registered("192.168.1.11");

        List<DeviceListItem> result = DeviceListComposer.Compose(
            [receiver, decoy],
            _ => false,
            _ => (false, false),
            Registry().Object,
            d => d.Id == receiver.Id
        );

        Assert.True(result.Single(i => i.DeviceId == receiver.Id).Online);
        Assert.False(result.Single(i => i.DeviceId == decoy.Id).Online);
    }

    [Fact]
    public void Compose_NoCastHits_ReturnsOnlyRegisteredRows_FlaggedAsRegistered()
    {
        Device tv = Registered();
        Mock<ICastMdnsRegistry> registry = Registry();

        List<DeviceListItem> result = DeviceListComposer.Compose(
            [tv],
            _ => true,
            _ => (true, true),
            registry.Object
        );

        DeviceListItem item = Assert.Single(result);
        Assert.Equal(tv.Id, item.DeviceId);
        Assert.True(item.IsRegisteredClient);
    }

    // Without mDNS the phone got no address for a TV whose Cast name differs from its row name.
    [Fact]
    public void Compose_RowWithoutMdnsLanIp_ListsTheLanAddressItConnectedFrom()
    {
        Device tv = Registered(lanIp: null!);
        tv.Ip = "192.168.2.80";

        List<DeviceListItem> result = DeviceListComposer.Compose(
            [tv],
            _ => true,
            _ => (true, true),
            Registry().Object
        );

        Assert.Equal("192.168.2.80", Assert.Single(result).LanIp);
    }

    [Fact]
    public void Compose_RowSeenOnlyFromAPublicAddress_ListsNoLanAddress()
    {
        Device tv = Registered(lanIp: null!);
        tv.Ip = "203.0.113.49";

        List<DeviceListItem> result = DeviceListComposer.Compose(
            [tv],
            _ => true,
            _ => (true, true),
            Registry().Object
        );

        Assert.Null(Assert.Single(result).LanIp);
    }

    [Fact]
    public void Compose_CastHitMatchingRegisteredLanIp_IsNotDuplicated()
    {
        Device tv = Registered("192.168.1.10");
        CastMdnsHit hit = new(
            "cast-abc",
            "Living Room TV",
            "Chromecast",
            "192.168.1.10",
            DateTime.UtcNow
        );
        Mock<ICastMdnsRegistry> registry = Registry(hit);

        List<DeviceListItem> result = DeviceListComposer.Compose(
            [tv],
            _ => true,
            _ => (true, true),
            registry.Object
        );

        DeviceListItem item = Assert.Single(result);
        Assert.Equal(tv.Id, item.DeviceId);
        Assert.True(item.IsRegisteredClient);
        Assert.True(item.CastReachable);
    }

    [Fact]
    public void Compose_CastHitWithNoRegisteredMatch_AddsSyntheticUnregisteredEntry()
    {
        CastMdnsHit hit = new(
            "cast-xyz",
            "Kitchen Speaker",
            "Google Nest Mini",
            "192.168.1.55",
            DateTime.UtcNow
        );
        Mock<ICastMdnsRegistry> registry = Registry(hit);

        List<DeviceListItem> result = DeviceListComposer.Compose(
            [],
            _ => false,
            _ => (false, false),
            registry.Object
        );

        DeviceListItem item = Assert.Single(result);
        Assert.False(item.IsRegisteredClient);
        Assert.True(item.CastReachable);
        Assert.False(item.Online);
        Assert.Equal("Kitchen Speaker", item.Name);
        Assert.Equal("chromecast", item.Type);
        Assert.Equal("192.168.1.55", item.LanIp);
        Assert.NotEqual(Ulid.Empty, item.DeviceId);
        Assert.Equal($"cast:{hit.Id}", item.Fingerprint);
    }

    [Fact]
    public void Compose_UnregisteredCastHit_FallsBackToModelName_WhenFriendlyNameMissing()
    {
        CastMdnsHit hit = new(
            "cast-noname",
            null,
            "Chromecast Ultra",
            "192.168.1.60",
            DateTime.UtcNow
        );
        Mock<ICastMdnsRegistry> registry = Registry(hit);

        List<DeviceListItem> result = DeviceListComposer.Compose(
            [],
            _ => false,
            _ => (false, false),
            registry.Object
        );

        Assert.Equal("Chromecast Ultra", Assert.Single(result).Name);
    }

    [Fact]
    public void Compose_SameCastId_ProducesStableDeviceId_AcrossCalls()
    {
        CastMdnsHit hit = new(
            "cast-stable",
            "Bedroom TV",
            "Chromecast",
            "192.168.1.70",
            DateTime.UtcNow
        );
        Mock<ICastMdnsRegistry> registry = Registry(hit);

        List<DeviceListItem> first = DeviceListComposer.Compose(
            [],
            _ => false,
            _ => (false, false),
            registry.Object
        );
        List<DeviceListItem> second = DeviceListComposer.Compose(
            [],
            _ => false,
            _ => (false, false),
            registry.Object
        );

        Assert.Equal(first[0].DeviceId, second[0].DeviceId);
    }

    [Fact]
    public void Compose_MultipleRegisteredAndUnregisteredHits_PartitionsCorrectly()
    {
        Device registeredTv = Registered("192.168.1.10");
        CastMdnsHit matchedHit = new(
            "cast-matched",
            "Living Room TV",
            "Chromecast",
            "192.168.1.10",
            DateTime.UtcNow
        );
        CastMdnsHit strangerHit = new(
            "cast-stranger",
            "Neighbour's Chromecast",
            "Chromecast",
            "192.168.1.99",
            DateTime.UtcNow
        );
        Mock<ICastMdnsRegistry> registry = Registry(matchedHit, strangerHit);

        List<DeviceListItem> result = DeviceListComposer.Compose(
            [registeredTv],
            id => id == registeredTv.Id,
            _ => (false, false),
            registry.Object
        );

        Assert.Equal(2, result.Count);
        Assert.Single(result, d => d.IsRegisteredClient && d.DeviceId == registeredTv.Id);
        Assert.Single(result, d => !d.IsRegisteredClient && d.Name == "Neighbour's Chromecast");
    }

    [Fact]
    public void Compose_RegistryReturnsNullFromGetSeen_DoesNotThrow_AndReturnsRegisteredRowsOnly()
    {
        Device tv = Registered();
        Mock<ICastMdnsRegistry> registry = new();
        registry.Setup(r => r.GetSeen()).Returns((IReadOnlyCollection<CastMdnsHit>)null!);
        registry.Setup(r => r.IsReachable(It.IsAny<string?>())).Returns(false);

        List<DeviceListItem> result = DeviceListComposer.Compose(
            [tv],
            _ => true,
            _ => (false, false),
            registry.Object
        );

        DeviceListItem item = Assert.Single(result);
        Assert.True(item.IsRegisteredClient);
    }
}
