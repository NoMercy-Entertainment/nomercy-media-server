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

using NoMercy.OpticalMedia.Onboarding;

namespace NoMercy.Tests.OpticalMedia.Onboarding;

[Trait("Category", "Unit")]
public class DiscOnboardingSessionStoreTests
{
    [Fact]
    public void TryGet_UnknownDrive_ReturnsFalse()
    {
        DiscOnboardingSessionStore store = new();

        bool found = store.TryGet("D:\\", out DiscOnboardingSession? session);

        found.Should().BeFalse();
        session.Should().BeNull();
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsTheSameSession()
    {
        DiscOnboardingSessionStore store = new();
        DiscOnboardingSession session = DiscOnboardingSession.Create("D:\\");

        store.Set(session);
        bool found = store.TryGet("D:\\", out DiscOnboardingSession? fetched);

        found.Should().BeTrue();
        fetched!.SessionId.Should().Be(session.SessionId);
    }

    [Fact]
    public void TryGet_IsCaseAndTrailingSlashInsensitiveOnDrivePath()
    {
        DiscOnboardingSessionStore store = new();
        store.Set(DiscOnboardingSession.Create("D:\\"));

        bool found = store.TryGet("d:", out DiscOnboardingSession? fetched);

        found.Should().BeTrue();
        fetched.Should().NotBeNull();
    }

    [Fact]
    public void Remove_DropsTheSession()
    {
        DiscOnboardingSessionStore store = new();
        store.Set(DiscOnboardingSession.Create("D:\\"));

        store.Remove("D:\\");

        store.TryGet("D:\\", out DiscOnboardingSession? fetched).Should().BeFalse();
        fetched.Should().BeNull();
    }

    [Fact]
    public void All_ReturnsEverySessionAcrossDrives()
    {
        DiscOnboardingSessionStore store = new();
        store.Set(DiscOnboardingSession.Create("D:\\"));
        store.Set(DiscOnboardingSession.Create("E:\\"));

        store.All.Should().HaveCount(2);
        store.All.Select(s => s.DrivePath).Should().BeEquivalentTo("D:\\", "E:\\");
    }
}
