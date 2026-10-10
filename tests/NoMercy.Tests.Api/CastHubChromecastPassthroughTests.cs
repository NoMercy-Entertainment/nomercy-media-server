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

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Data.Activity;
using NoMercy.Database;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Auth;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;
using Xunit;

namespace NoMercy.Tests.Api;

// CastHub's device-control methods delegate to IChromeCastService after
// command input validation. IChromeCastService is fully mocked here, so no
// real Chromecast is ever discovered, connected to, or commanded.
[Trait("Category", "Unit")]
public class CastHubChromecastPassthroughTests
{
    private static CastHub CreateHub(
        out Mock<IChromeCastService> chromeCast,
        out Mock<IAuthTokenStore> authTokenStore
    )
    {
        chromeCast = new Mock<IChromeCastService>();
        authTokenStore = new Mock<IAuthTokenStore>();

        return new CastHub(
            NullLogger<CastHub>.Instance,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IDbContextFactory<MediaContext>>(),
            new ConnectedClients(),
            Mock.Of<IClientMessenger>(),
            Mock.Of<IActivityLogger>(),
            authTokenStore.Object,
            chromeCast.Object
        );
    }

    [Fact]
    public void GetChromeCasts_ReturnsDiscoveredReceiverNames_FromChromeCastService()
    {
        CastHub hub = CreateHub(out Mock<IChromeCastService> chromeCast, out _);
        chromeCast.Setup(c => c.GetChromeCasts()).Returns(["Living Room", "Bedroom"]);

        string[] result = hub.GetChromeCasts();

        result.Should().Equal(["Living Room", "Bedroom"]);
    }

    [Fact]
    public async Task SelectChromecast_ForwardsReceiverName_ToChromeCastService()
    {
        CastHub hub = CreateHub(out Mock<IChromeCastService> chromeCast, out _);
        chromeCast.Setup(c => c.GetChromeCasts()).Returns(["Living Room"]);

        HubCommandResult result = await hub.SelectChromecast("Living Room");

        result.Ok.Should().BeTrue();
        chromeCast.Verify(c => c.SelectChromecast("Living Room"), Times.Once);
    }

    [Fact]
    public async Task Launch_InvokesChromeCastServiceLaunch_WithNoReceiverNameOverride()
    {
        CastHub hub = CreateHub(out Mock<IChromeCastService> chromeCast, out _);

        await hub.Launch();

        chromeCast.Verify(c => c.Launch(null), Times.Once);
    }

    [Fact]
    public async Task CastPlaylist_ForwardsPlaylistValue_AndCurrentAccessToken()
    {
        CastHub hub = CreateHub(
            out Mock<IChromeCastService> chromeCast,
            out Mock<IAuthTokenStore> authTokenStore
        );
        authTokenStore.Setup(a => a.AccessToken).Returns("token-123");

        await hub.CastPlaylist("playlist-payload");

        chromeCast.Verify(c => c.CastPlaylist("playlist-payload", null, "token-123"), Times.Once);
    }

    [Fact]
    public void GetChromecastStatus_ReturnsWhateverChromeCastServiceReports()
    {
        CastHub hub = CreateHub(out Mock<IChromeCastService> chromeCast, out _);
        chromeCast.Setup(c => c.GetChromecastStatus(null)).Returns((ChromecastStatus?)null);

        ChromecastStatus? result = hub.GetChromecastStatus();

        result.Should().BeNull();
        chromeCast.Verify(c => c.GetChromecastStatus(null), Times.Once);
    }

    [Fact]
    public void GetMediaStatus_ReturnsWhateverChromeCastServiceReports()
    {
        CastHub hub = CreateHub(out Mock<IChromeCastService> chromeCast, out _);
        chromeCast.Setup(c => c.GetMediaStatus(null)).Returns((MediaStatus?)null);

        MediaStatus? result = hub.GetMediaStatus();

        result.Should().BeNull();
        chromeCast.Verify(c => c.GetMediaStatus(null), Times.Once);
    }

    [Fact]
    public async Task Stop_InvokesChromeCastServiceStop_WithNoReceiverNameOverride()
    {
        CastHub hub = CreateHub(out Mock<IChromeCastService> chromeCast, out _);

        await hub.Stop();

        chromeCast.Verify(c => c.Stop(null), Times.Once);
    }

    [Fact]
    public async Task Disconnect_InvokesChromeCastServiceDisconnect_WithNoReceiverNameOverride()
    {
        CastHub hub = CreateHub(out Mock<IChromeCastService> chromeCast, out _);

        await hub.Disconnect();

        chromeCast.Verify(c => c.Disconnect(null), Times.Once);
    }
}
