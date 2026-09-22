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
using NoMercy.Api.Plugins;
using NoMercy.PluginSdk.Ipc;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// What reaches a plugin's own process when somebody calls its REST route.
/// <para>
/// The server has already authenticated the caller. A plugin process holding
/// the owner's bearer token could call the server's entire API as them, which
/// is more than any capability grants and more than the owner agreed to when
/// they installed it.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginReverseProxyRequestTests
{
    private static readonly WireCaller Caller = new(
        Guid.NewGuid().ToString(),
        "Owned",
        "web",
        true
    );

    [Theory]
    [InlineData("Authorization", "Bearer a-real-token")]
    [InlineData("authorization", "Bearer a-real-token")]
    [InlineData("Cookie", "session=abc")]
    [InlineData("Proxy-Authorization", "Basic abc")]
    [InlineData("X-Api-Key", "a-key")]
    public void ACredential_NeverReachesThePluginProcess(string header, string value)
    {
        Dictionary<string, string> forwarded = PluginReverseProxyRequest.HeadersFor(
            new Dictionary<string, string> { [header] = value },
            Caller
        );

        forwarded.Should().NotContainKey(header);
        forwarded.Values.Should().NotContain(v => v.Contains("a-real-token"));
    }

    /// <summary>
    /// A deny list alone is the wrong way round for a credential: one header
    /// nobody thought of and the token is through.
    /// </summary>
    [Fact]
    public void AHeaderNobodyDecidedWasSafe_IsDroppedRatherThanForwarded()
    {
        Dictionary<string, string> forwarded = PluginReverseProxyRequest.HeadersFor(
            new Dictionary<string, string> { ["X-Something-New"] = "whatever" },
            Caller
        );

        forwarded.Should().NotContainKey("X-Something-New");
    }

    [Fact]
    public void TheHeadersAPluginActuallyNeeds_DoCross()
    {
        Dictionary<string, string> forwarded = PluginReverseProxyRequest.HeadersFor(
            new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["Accept-Language"] = "nl-NL",
                ["Range"] = "bytes=0-1023",
            },
            Caller
        );

        forwarded.Should().ContainKeys("Accept", "Accept-Language", "Range");
    }

    [Fact]
    public void WhoIsAsking_TravelsSoThePluginCanTellUsersApart()
    {
        Dictionary<string, string> forwarded = PluginReverseProxyRequest.HeadersFor(
            new Dictionary<string, string>(),
            Caller
        );

        WireCaller? read = PluginReverseProxyRequest.ReadCaller(
            forwarded[PluginReverseProxyRequest.CallerHeader]
        );

        read!.UserId.Should().Be(Caller.UserId);
        read.IsOwner.Should().BeTrue();
    }

    /// <summary>
    /// The server writes the caller header last. A request that arrived
    /// carrying one would otherwise let anybody claim to be the owner.
    /// </summary>
    [Fact]
    public void ARequestClaimingItsOwnCaller_IsOverwrittenByTheServers()
    {
        WireCaller liar = new(Guid.NewGuid().ToString(), "Owned", "web", true);

        Dictionary<string, string> forwarded = PluginReverseProxyRequest.HeadersFor(
            new Dictionary<string, string>
            {
                [PluginReverseProxyRequest.CallerHeader] =
                    System.Text.Json.JsonSerializer.Serialize(liar),
            },
            Caller
        );

        PluginReverseProxyRequest
            .ReadCaller(forwarded[PluginReverseProxyRequest.CallerHeader])!
            .UserId.Should()
            .Be(Caller.UserId);
    }

    /// <summary>
    /// The deny list is the second lock, and this is the only thing that
    /// exercises it.
    /// <para>
    /// Every other credential test here passes on the allow list alone, so
    /// disabling a deny entry changed nothing and nothing would have caught
    /// somebody later adding a credential header to the allow list by
    /// mistake. That mistake is simulated here.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("x-api-key")]
    public void ACredentialSomebodyAddedToTheAllowListByMistake_IsStillDenied(string header)
    {
        HashSet<string> mistaken = new(StringComparer.OrdinalIgnoreCase) { "accept", header };

        Dictionary<string, string> forwarded = PluginReverseProxyRequest.HeadersFor(
            new Dictionary<string, string> { [header] = "Bearer a-real-token" },
            Caller,
            mistaken
        );

        forwarded.Should().NotContainKey(header);
    }

    [Fact]
    public void ACallerHeaderThatIsNotJson_ReadsAsNobodyRatherThanThrowing()
    {
        PluginReverseProxyRequest.ReadCaller("{ not json").Should().BeNull();
        PluginReverseProxyRequest.ReadCaller(null).Should().BeNull();
    }
}
