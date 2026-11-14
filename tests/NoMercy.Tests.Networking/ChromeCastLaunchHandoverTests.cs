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

using System.Collections.ObjectModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Networking.Cast;
using Sharpcaster.Models.ChromecastStatus;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// A web receiver cannot read the LAUNCH customData (CAF has no API for it), so a
/// server-initiated cast ends a stale NoMercy session, launches fresh, and then
/// hands the launch data to the new session on the receiver's general namespace.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChromeCastLaunchHandoverTests
{
    private static ChromecastStatus Status(params ChromecastApplication[] apps) =>
        new() { Applications = new Collection<ChromecastApplication>(apps) };

    private static ChromecastApplication App(
        string appId,
        string sessionId,
        string transportId,
        params string[] namespaces
    ) =>
        new()
        {
            AppId = appId,
            SessionId = sessionId,
            TransportId = transportId,
            Namespaces = new Collection<Namespace>([
                .. namespaces.Select(n => new Namespace { Name = n }),
            ]),
        };

    [Fact]
    public void FindNoMercySessionId_ReturnsOurRunningSession()
    {
        ChromecastStatus status = Status(App("925b4c3c", "session-1", "transport-1"));

        Assert.Equal("session-1", ChromeCastService.FindNoMercySessionId(status));
    }

    [Fact]
    public void FindNoMercySessionId_LeavesAnotherAppAlone()
    {
        ChromecastStatus status = Status(App("CC1AD845", "session-2", "transport-2"));

        Assert.Null(ChromeCastService.FindNoMercySessionId(status));
        Assert.Null(ChromeCastService.FindNoMercySessionId(Status()));
        Assert.Null(ChromeCastService.FindNoMercySessionId(null));
    }

    [Fact]
    public void BuildStopJson_EndsOnlyTheNamedSession()
    {
        JObject stop = JObject.Parse(ChromeCastService.BuildStopJson(7, "session-1"));

        Assert.Equal("STOP", (string?)stop["type"]);
        Assert.Equal(7, (int?)stop["requestId"]);
        Assert.Equal("session-1", (string?)stop["sessionId"]);
    }

    [Fact]
    public void FindHandoverTransport_WaitsUntilTheReceiverListensOnOurNamespace()
    {
        ChromecastStatus loading = Status(App("925B4C3C", "session-3", "transport-3"));
        ChromecastStatus ready = Status(
            App(
                "925B4C3C",
                "session-3",
                "transport-3",
                "urn:x-cast:com.google.cast.media",
                "urn:x-cast:tv.nomercy.app"
            )
        );

        Assert.Null(ChromeCastService.FindHandoverTransport(loading));
        Assert.Equal("transport-3", ChromeCastService.FindHandoverTransport(ready));
    }

    [Fact]
    public void FindHandoverTransport_IgnoresAnotherAppOnOurNamespace()
    {
        ChromecastStatus status = Status(
            App("CC1AD845", "session-4", "transport-4", "urn:x-cast:tv.nomercy.app")
        );

        Assert.Null(ChromeCastService.FindHandoverTransport(status));
    }

    // Same attribute style as NoMercy.Setup.Cast.LaunchCustomData, which this project does not reference.
    private sealed class SnakeCaseLaunch
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonProperty("server_url")]
        public string ServerUrl { get; set; } = "";
    }

    [Fact]
    public void BuildLaunchHandoverJson_WrapsTheSnakeCaseLaunchData()
    {
        SnakeCaseLaunch data = new()
        {
            AccessToken = "token-placeholder",
            ServerUrl = "https://192-0-2-10.example.invalid:7626",
        };

        JObject message = JObject.Parse(ChromeCastService.BuildLaunchHandoverJson(data));

        JToken launch = message["launch"]!;
        Assert.Equal("token-placeholder", (string?)launch["access_token"]);
        Assert.Equal("https://192-0-2-10.example.invalid:7626", (string?)launch["server_url"]);
    }
}
