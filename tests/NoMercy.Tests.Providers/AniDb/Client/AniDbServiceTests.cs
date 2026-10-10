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

using System.Diagnostics;
using NoMercy.Providers.AniDb.Client;

namespace NoMercy.Tests.Providers.AniDb.Client;

public class AniDbServiceTests
{
    [Fact]
    public void Dispose_WhenLogoutNeverReplies_ReturnsAndDisconnects()
    {
        bool logoutCalled = false;
        bool disconnected = false;
        Stopwatch stopwatch = Stopwatch.StartNew();

        AniDbService service = new(
            () => true,
            _ => logoutCalled = true,
            () => disconnected = true,
            TimeSpan.FromMilliseconds(100)
        );
        service.Dispose();

        stopwatch.Stop();
        logoutCalled.Should().BeTrue();
        disconnected.Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Dispose_WhenLogoutReplies_DisconnectsWithoutWaitingForTimeout()
    {
        bool disconnected = false;
        Stopwatch stopwatch = Stopwatch.StartNew();

        AniDbService service = new(
            () => true,
            callback => callback(),
            () => disconnected = true,
            TimeSpan.FromSeconds(2)
        );
        service.Dispose();

        stopwatch.Stop();
        disconnected.Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}
