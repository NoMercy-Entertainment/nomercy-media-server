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
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Api.DTOs.Security;
using NoMercy.Api.Security;
using NoMercy.Authorization;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Security;
using NoMercy.Events;
using NoMercy.Networking.Discovery;
using Xunit;

namespace NoMercy.Tests.Api.Security;

public class AbuseGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly RequestOutcome WpLogin = new("/wp-login.php", false, 401, false);
    private static readonly RequestOutcome RandomPhp = new("/9WOLF.php", false, 401, false);

    private static AbuseGuard CreateGuard(
        TestTimeProvider time,
        InMemoryIpBanRepository repository,
        IAbuseGuardSettings? settings = null,
        INetworkDiscovery? discovery = null
    ) =>
        new(
            repository,
            settings ?? new AbuseGuardSettings(new FakeConfigurationStore()),
            Mock.Of<IActivityLogger>(),
            Mock.Of<IEventBus>(),
            Mock.Of<ILogger<AbuseGuard>>(),
            time,
            UserCache.Current,
            discovery ?? Mock.Of<INetworkDiscovery>()
        );

    private static RequestOutcome Miss(int attempt) => new($"/admin-{attempt}", false, 401, false);

    private static async Task RecordMissesAsync(AbuseGuard guard, IPAddress address, int count)
    {
        for (int attempt = 0; attempt < count; attempt++)
            await guard.RecordAsync(address, Miss(attempt), CancellationToken.None);
    }

    [Fact]
    public async Task RecordAsync_FiftyMissesFromTheServersOwnExternalAddress_NeverBan()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        INetworkDiscovery discovery = Mock.Of<INetworkDiscovery>(d => d.ExternalIp == "192.0.2.10");
        AbuseGuard guard = CreateGuard(time, repository, discovery: discovery);
        IPAddress home = IPAddress.Parse("192.0.2.10");

        await RecordMissesAsync(guard, home, 50);

        (await guard.IsBannedAsync(home, CancellationToken.None)).Should().BeFalse();
        repository.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordAsync_TheServersOwnExternalIpv6Address_IsNeverBanned()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        INetworkDiscovery discovery = Mock.Of<INetworkDiscovery>(d =>
            d.ExternalIp == "0.0.0.0" && d.ExternalIpV6 == "2001:db8::10"
        );
        AbuseGuard guard = CreateGuard(time, repository, discovery: discovery);
        IPAddress home = IPAddress.Parse("2001:db8::10");

        await RecordMissesAsync(guard, home, 50);

        (await guard.IsBannedAsync(home, CancellationToken.None)).Should().BeFalse();
        repository.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordAsync_AfterTheExternalAddressChanges_OnlyTheNewOneIsExempt()
    {
        TestTimeProvider time = new(Start);
        Mock<INetworkDiscovery> discovery = new();
        discovery.SetupGet(d => d.ExternalIp).Returns("192.0.2.10");
        AbuseGuard guard = CreateGuard(time, new(), discovery: discovery.Object);
        IPAddress earlier = IPAddress.Parse("192.0.2.10");
        IPAddress current = IPAddress.Parse("192.0.2.11");

        discovery.SetupGet(d => d.ExternalIp).Returns("192.0.2.11");
        await RecordMissesAsync(guard, earlier, 50);
        await RecordMissesAsync(guard, current, 50);

        (await guard.IsBannedAsync(earlier, CancellationToken.None)).Should().BeTrue();
        (await guard.IsBannedAsync(current, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsBannedAsync_ABanAlreadyStoredForTheServersOwnAddress_IsNotEnforced()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        repository.Rows.Add(
            new()
            {
                Address = "192.0.2.10",
                Reason = "Suspicious",
                OffenceCount = 10,
                BanNumber = 1,
                BannedAt = time.GetUtcNow().UtcDateTime,
                ExpiresAt = DateTime.MaxValue,
            }
        );
        INetworkDiscovery discovery = Mock.Of<INetworkDiscovery>(d => d.ExternalIp == "192.0.2.10");
        AbuseGuard guard = CreateGuard(time, repository, discovery: discovery);

        await guard.RefreshAsync(CancellationToken.None);

        (await guard.IsBannedAsync(IPAddress.Parse("192.0.2.10"), CancellationToken.None))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task RecordAsync_ABanMadeOnlyOfUnroutedMisses_ExpiresAfterTheBanDuration()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        AbuseGuard guard = CreateGuard(time, repository);
        IPAddress client = IPAddress.Parse("203.0.113.90");

        await RecordMissesAsync(guard, client, 10);
        IpBan ban = repository.Rows.Single();

        ban.Reason.Should().Be("Suspicious");
        ban.ExpiresAt.Should().Be(time.GetUtcNow().UtcDateTime.AddHours(1));
        (await guard.IsBannedAsync(client, CancellationToken.None)).Should().BeTrue();

        time.Advance(TimeSpan.FromMinutes(61));

        (await guard.IsBannedAsync(client, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_ARepeatedMissOnlyBan_LastsTwiceAsLong()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        AbuseGuard guard = CreateGuard(time, repository);
        IPAddress client = IPAddress.Parse("203.0.113.91");

        await RecordMissesAsync(guard, client, 10);
        time.Advance(TimeSpan.FromMinutes(61));
        await RecordMissesAsync(guard, client, 10);
        IpBan second = repository.Rows.Single();

        second.BanNumber.Should().Be(2);
        second.ExpiresAt.Should().Be(time.GetUtcNow().UtcDateTime.AddHours(2));
    }

    [Fact]
    public async Task RecordAsync_ARepeatedMissOnlyBan_NeverExceedsTheConfiguredMaximum()
    {
        FakeConfigurationStore store = new();
        store.SetValue(AbuseGuardSettings.MaxBanMinutesKey, "90");
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        AbuseGuard guard = CreateGuard(time, repository, new AbuseGuardSettings(store));
        IPAddress client = IPAddress.Parse("203.0.113.92");

        await RecordMissesAsync(guard, client, 10);
        time.Advance(TimeSpan.FromMinutes(61));
        await RecordMissesAsync(guard, client, 10);

        repository.Rows.Single().ExpiresAt.Should().Be(time.GetUtcNow().UtcDateTime.AddMinutes(90));
    }

    [Fact]
    public async Task RecordAsync_AKnownProbeAmongTheMisses_KeepsTheBanPermanent()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        AbuseGuard guard = CreateGuard(time, repository);
        IPAddress scanner = IPAddress.Parse("203.0.113.93");

        await guard.RecordAsync(scanner, WpLogin, CancellationToken.None);
        await RecordMissesAsync(guard, scanner, 5);
        IpBan ban = repository.Rows.Single();

        ban.Reason.Should().Be("KnownProbe");
        ban.ExpiresAt.Should().Be(DateTime.MaxValue);
    }

    [Fact]
    public async Task RecordAsync_ABan_KeepsTheLastPathForTheDashboard()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        AbuseGuard guard = CreateGuard(time, repository);

        await RecordMissesAsync(guard, IPAddress.Parse("203.0.113.94"), 10);

        IpBanDto.From(repository.Rows.Single()).LastPath.Should().Be("/admin-9");
    }

    [Fact]
    public async Task RecordAsync_TwoKnownProbes_CrossesTheThresholdAndBans()
    {
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new());
        IPAddress attacker = IPAddress.Parse("203.0.113.77");

        await guard.RecordAsync(attacker, WpLogin, CancellationToken.None);
        await guard.RecordAsync(attacker, RandomPhp, CancellationToken.None);

        (await guard.IsBannedAsync(attacker, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RecordAsync_OneKnownProbe_DoesNotBan()
    {
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new());
        IPAddress attacker = IPAddress.Parse("203.0.113.78");

        await guard.RecordAsync(attacker, WpLogin, CancellationToken.None);

        (await guard.IsBannedAsync(attacker, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_OffencesSpreadBeyondTheWindow_NeverAddUp()
    {
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new());
        IPAddress attacker = IPAddress.Parse("203.0.113.79");

        await guard.RecordAsync(attacker, WpLogin, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(11));
        await guard.RecordAsync(attacker, RandomPhp, CancellationToken.None);

        (await guard.IsBannedAsync(attacker, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_ExpiredTokenOnARealEndpoint_NeverBansTheUser()
    {
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new());
        IPAddress user = IPAddress.Parse("203.0.113.80");
        RequestOutcome rejectedButRouted = new("/api/v1/movies", true, 401, false);

        for (int attempt = 0; attempt < 200; attempt++)
            await guard.RecordAsync(user, rejectedButRouted, CancellationToken.None);

        (await guard.IsBannedAsync(user, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_PrivateAddress_IsNeverScored()
    {
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new());
        IPAddress lan = IPAddress.Parse("192.168.1.50");

        for (int attempt = 0; attempt < 20; attempt++)
            await guard.RecordAsync(lan, WpLogin, CancellationToken.None);

        (await guard.IsBannedAsync(lan, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_AllowlistedPublicAddress_IsNeverScored()
    {
        FakeConfigurationStore store = new();
        store.SetValue(AbuseGuardSettings.AllowlistKey, "203.0.113.0/24");
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new(), new AbuseGuardSettings(store));
        IPAddress office = IPAddress.Parse("203.0.113.5");

        for (int attempt = 0; attempt < 20; attempt++)
            await guard.RecordAsync(office, WpLogin, CancellationToken.None);

        (await guard.IsBannedAsync(office, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_Disabled_NeverBans()
    {
        FakeConfigurationStore store = new();
        store.SetValue(AbuseGuardSettings.EnabledKey, "false");
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new(), new AbuseGuardSettings(store));
        IPAddress attacker = IPAddress.Parse("203.0.113.81");

        for (int attempt = 0; attempt < 20; attempt++)
            await guard.RecordAsync(attacker, WpLogin, CancellationToken.None);

        (await guard.IsBannedAsync(attacker, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_TenUnroutedAnonymousMisses_AddUpToABan()
    {
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new());
        IPAddress scanner = IPAddress.Parse("203.0.113.87");

        for (int attempt = 0; attempt < 10; attempt++)
            await guard.RecordAsync(
                scanner,
                new($"/admin-{attempt}", false, 401, false),
                CancellationToken.None
            );

        (await guard.IsBannedAsync(scanner, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsBannedAsync_BanExpires_WithoutAnyoneClearingIt()
    {
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new());
        IPAddress attacker = IPAddress.Parse("203.0.113.82");

        await guard.BanAsync(
            attacker,
            "probe",
            TimeSpan.FromMinutes(60),
            false,
            CancellationToken.None
        );
        (await guard.IsBannedAsync(attacker, CancellationToken.None)).Should().BeTrue();

        time.Advance(TimeSpan.FromMinutes(61));

        (await guard.IsBannedAsync(attacker, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_ProbeBan_NeverExpires()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        AbuseGuard guard = CreateGuard(time, repository);
        IPAddress attacker = IPAddress.Parse("203.0.113.83");

        await guard.RecordAsync(attacker, WpLogin, CancellationToken.None);
        await guard.RecordAsync(attacker, RandomPhp, CancellationToken.None);
        IpBan first = repository.Rows.Single();

        first.ExpiresAt.Should().Be(DateTime.MaxValue);
        first.BanNumber.Should().Be(1);

        time.Advance(TimeSpan.FromDays(365));

        (await guard.IsBannedAsync(attacker, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RecordAsync_RepeatOffenderAfterUnban_StartsAFreshBanNumber()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        AbuseGuard guard = CreateGuard(time, repository);
        IPAddress attacker = IPAddress.Parse("203.0.113.83");

        await guard.RecordAsync(attacker, WpLogin, CancellationToken.None);
        await guard.RecordAsync(attacker, RandomPhp, CancellationToken.None);
        repository.Rows.Single().BanNumber.Should().Be(1);

        // Unban removes the row, so the moderator's manual exemption also clears
        // the offender's history — a re-offense after that is a fresh case.
        await guard.UnbanAsync(attacker.ToString(), CancellationToken.None);

        await guard.RecordAsync(attacker, WpLogin, CancellationToken.None);
        await guard.RecordAsync(attacker, RandomPhp, CancellationToken.None);
        IpBan second = repository.Rows.Single();

        second.ExpiresAt.Should().Be(DateTime.MaxValue);
        second.BanNumber.Should().Be(1);
    }

    [Fact]
    public async Task BanAsync_NeverExceedsTheConfiguredMaximum()
    {
        FakeConfigurationStore store = new();
        store.SetValue(AbuseGuardSettings.MaxBanMinutesKey, "90");
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        AbuseGuard guard = CreateGuard(time, repository, new AbuseGuardSettings(store));

        await guard.BanAsync(
            IPAddress.Parse("203.0.113.84"),
            "probe",
            TimeSpan.FromDays(30),
            false,
            CancellationToken.None
        );

        (repository.Rows.Single().ExpiresAt - time.GetUtcNow().UtcDateTime)
            .Should()
            .Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public async Task UnbanAsync_ReleasesImmediately()
    {
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new());
        IPAddress attacker = IPAddress.Parse("203.0.113.85");

        await guard.BanAsync(
            attacker,
            "probe",
            TimeSpan.FromHours(1),
            false,
            CancellationToken.None
        );
        bool released = await guard.UnbanAsync("203.0.113.85", CancellationToken.None);

        released.Should().BeTrue();
        (await guard.IsBannedAsync(attacker, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task UnbanAsync_ReportsFalseWhenThereWasNothingToLift()
    {
        TestTimeProvider time = new(Start);
        AbuseGuard guard = CreateGuard(time, new());

        (await guard.UnbanAsync("203.0.113.88", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_RehydratesBansWrittenBeforeThisProcessStarted()
    {
        TestTimeProvider time = new(Start);
        InMemoryIpBanRepository repository = new();
        repository.Rows.Add(
            new()
            {
                Address = "203.0.113.86",
                Reason = "KnownProbe",
                OffenceCount = 10,
                BanNumber = 1,
                BannedAt = time.GetUtcNow().UtcDateTime,
                ExpiresAt = time.GetUtcNow().UtcDateTime.AddHours(1),
            }
        );
        AbuseGuard guard = CreateGuard(time, repository);

        await guard.RefreshAsync(CancellationToken.None);

        (await guard.IsBannedAsync(IPAddress.Parse("203.0.113.86"), CancellationToken.None))
            .Should()
            .BeTrue();
    }
}
