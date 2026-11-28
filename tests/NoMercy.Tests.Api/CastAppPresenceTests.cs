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

using NoMercy.Api.Services.Cast;
using NoMercy.Database.Models.Users;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Unit")]
public class CastAppPresenceTests
{
    private static readonly DateTime Now = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

    private static Device Tv(string? fingerprint, DateTime? busSeenAt) =>
        new()
        {
            Id = Ulid.NewUlid(),
            Name = "Test Room TV",
            Type = "tv",
            Fingerprint = fingerprint,
            WsConnectedAt = busSeenAt,
        };

    [Fact]
    public void HasApp_AppOnTheBusToday_IsTrue() =>
        CastAppPresence.HasApp(Tv("test-fingerprint-0001", Now.AddHours(-3)), Now).Should().BeTrue();

    // The app was removed from this TV days ago; its old row keeps the fingerprint.
    [Fact]
    public void HasApp_FingerprintButBusLastSeenDaysAgo_IsFalse() =>
        CastAppPresence.HasApp(Tv("test-fingerprint-0001", Now.AddDays(-5)), Now).Should().BeFalse();

    [Fact]
    public void HasApp_NoFingerprint_IsFalse() =>
        CastAppPresence.HasApp(Tv(null, Now.AddMinutes(-1)), Now).Should().BeFalse();
}
