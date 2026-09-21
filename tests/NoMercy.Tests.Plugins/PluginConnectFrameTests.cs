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

using FluentAssertions;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Music;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The shared queue is one frame every device renders, so a plugin's station
/// has to arrive carrying everything a device needs to play it. These names are
/// read by four clients; a rename here is a silent stop on three of them.
/// </summary>
[Trait("Category", "Unit")]
public class PluginConnectFrameTests
{
    [Fact]
    public void ServerMedia_CarriesNoPluginFields()
    {
        PlaylistTrackDto track = new()
        {
            Name = "Track",
            Path = "/x",
            Duration = "00:03:00",
        };

        string json = JsonConvert.SerializeObject(track);

        json.Should().Contain("\"plugin_id\":null");
        json.Should().Contain("\"proxy_url\":null");
        json.Should().Contain("\"live\":false");
    }

    [Fact]
    public void PluginMedia_CarriesEveryFieldADeviceNeedsToPlayIt()
    {
        PlaylistTrackDto track = new()
        {
            Name = "NPO 3FM",
            Path = "/plugins/radio/npo3fm",
            Duration = "00:00:00",
            PluginId = "01J9ZK5V8Y0000000000000000",
            ProxyUrl = new Uri("https://server.test/api/v1/plugins/radio/proxy/npo3fm"),
            Live = true,
        };

        string json = JsonConvert.SerializeObject(track);

        json.Should().Contain("\"plugin_id\":\"01J9ZK5V8Y0000000000000000\"");
        json.Should()
            .Contain("\"proxy_url\":\"https://server.test/api/v1/plugins/radio/proxy/npo3fm\"");
        json.Should().Contain("\"live\":true");
    }

    /// <summary>
    /// A ticket in the query is a credential in every log the frame passes
    /// through, and the frame passes through every device in the session.
    /// </summary>
    [Fact]
    public void ProxyUrl_NeverCarriesAToken()
    {
        PlaylistTrackDto track = new()
        {
            Name = "NPO 3FM",
            Path = "/plugins/radio/npo3fm",
            Duration = "00:00:00",
            ProxyUrl = new Uri("https://server.test/api/v1/plugins/radio/proxy/npo3fm"),
        };

        track.ProxyUrl!.Query.Should().BeEmpty();
    }
}
