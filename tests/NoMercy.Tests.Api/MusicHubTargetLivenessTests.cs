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

using NoMercy.Api.Hubs;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Pins the wake decision in MusicHub.ChangeDeviceCoreAsync: a web cast receiver
/// that is live on the music hub but never on the device bus must not be woken
/// (a Cast LAUNCH reloads it and drops the music); a native TV app on the bus
/// still needs its Foreground bit.
/// </summary>
[Trait("Category", "Unit")]
public class MusicHubTargetLivenessTests
{
    [Fact]
    public void WebReceiverLiveOnMusicHubAndNotOnDeviceBus_IsLive()
    {
        MusicHub
            .IsTargetLive(
                liveOnMusicHub: true,
                targetIsTv: true,
                onDeviceBus: false,
                foreground: false
            )
            .Should()
            .BeTrue();
    }

    [Fact]
    public void NativeTvOnBusBackgrounded_IsNotLive()
    {
        MusicHub
            .IsTargetLive(
                liveOnMusicHub: true,
                targetIsTv: true,
                onDeviceBus: true,
                foreground: false
            )
            .Should()
            .BeFalse();
    }

    [Fact]
    public void NativeTvOnBusForegrounded_IsLive()
    {
        MusicHub
            .IsTargetLive(
                liveOnMusicHub: true,
                targetIsTv: true,
                onDeviceBus: true,
                foreground: true
            )
            .Should()
            .BeTrue();
    }

    [Fact]
    public void TvNotOnMusicHub_IsNotLive()
    {
        MusicHub
            .IsTargetLive(
                liveOnMusicHub: false,
                targetIsTv: true,
                onDeviceBus: false,
                foreground: false
            )
            .Should()
            .BeFalse();
    }

    [Fact]
    public void NonTvLiveOnMusicHub_IsLive()
    {
        MusicHub
            .IsTargetLive(
                liveOnMusicHub: true,
                targetIsTv: false,
                onDeviceBus: false,
                foreground: false
            )
            .Should()
            .BeTrue();
    }
}
