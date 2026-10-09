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
using NoMercy.Api.Security;
using Xunit;

namespace NoMercy.Tests.Api.Security;

public class AbuseGuardSettingsTests
{
    [Fact]
    public void Defaults_ProtectAServerThatWasNeverConfigured()
    {
        AbuseGuardSettings settings = new(new FakeConfigurationStore());

        settings.Enabled.Should().BeTrue();
        settings.MaxScore.Should().Be(10);
        settings.Window.Should().Be(TimeSpan.FromMinutes(10));
        settings.BanDuration.Should().Be(TimeSpan.FromMinutes(60));
        settings.MaxBanDuration.Should().Be(TimeSpan.FromDays(7));
        settings.Allowlist.Should().BeEmpty();
    }

    [Fact]
    public void StoredValues_OverrideTheDefaults()
    {
        FakeConfigurationStore store = new();
        store.SetValue(AbuseGuardSettings.EnabledKey, "false");
        store.SetValue(AbuseGuardSettings.MaxScoreKey, "3");
        store.SetValue(AbuseGuardSettings.WindowMinutesKey, "2");
        store.SetValue(AbuseGuardSettings.BanMinutesKey, "15");
        store.SetValue(AbuseGuardSettings.MaxBanMinutesKey, "120");

        AbuseGuardSettings settings = new(store);

        settings.Enabled.Should().BeFalse();
        settings.MaxScore.Should().Be(3);
        settings.Window.Should().Be(TimeSpan.FromMinutes(2));
        settings.BanDuration.Should().Be(TimeSpan.FromMinutes(15));
        settings.MaxBanDuration.Should().Be(TimeSpan.FromMinutes(120));
    }

    [Fact]
    public void GarbageValues_FallBackToTheDefaultRatherThanThrowing()
    {
        FakeConfigurationStore store = new();
        store.SetValue(AbuseGuardSettings.MaxScoreKey, "not-a-number");
        store.SetValue(AbuseGuardSettings.WindowMinutesKey, "0");

        AbuseGuardSettings settings = new(store);

        settings.MaxScore.Should().Be(10);
        settings.Window.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Allowlist_ParsesCidrAndBareAddresses()
    {
        FakeConfigurationStore store = new();
        store.SetValue(AbuseGuardSettings.AllowlistKey, "203.0.113.0/24, 198.51.100.7 ,garbage");

        AbuseGuardSettings settings = new(store);

        settings.Allowlist.Should().HaveCount(2);
        settings.Allowlist[0].Contains(IPAddress.Parse("203.0.113.99")).Should().BeTrue();
        settings.Allowlist[1].Contains(IPAddress.Parse("198.51.100.7")).Should().BeTrue();
        settings.Allowlist[1].Contains(IPAddress.Parse("198.51.100.8")).Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_WritesThroughAndIsVisibleImmediately()
    {
        FakeConfigurationStore store = new();
        AbuseGuardSettings settings = new(store);

        await settings.SetAsync(AbuseGuardSettings.MaxScoreKey, "4", CancellationToken.None);

        settings.MaxScore.Should().Be(4);
    }
}
