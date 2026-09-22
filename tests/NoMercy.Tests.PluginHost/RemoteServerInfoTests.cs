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

using System.Text.Json;
using FluentAssertions;
using NoMercy.PluginHost;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;
using ProtoBuf.Grpc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// What this server is, as the plugin process may ask.
/// <para>
/// A plugin branches on these facts, so a facade that answered a default
/// instead of the truth is a plugin taking the wrong branch on every install
/// where the answer mattered.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class RemoteServerInfoTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Fact]
    public void TheVersionCrossesTheBoundaryAsAVersionRatherThanAString()
    {
        RemoteServerInfo info = new(Call(Answering("\"11.4.2\"")));

        info.Version.Should().Be(new Version(11, 4, 2));
    }

    /// <summary>
    /// A version the server could not state is not a version the plugin should
    /// branch on. Zero is the answer that makes every "at least" test fail,
    /// which is the safe direction.
    /// </summary>
    [Fact]
    public void AVersionTheServerCouldNotStateReadsAsZeroRatherThanThrowing()
    {
        RemoteServerInfo info = new(Call(Answering("\"not-a-version\"")));

        info.Version.Should().Be(new Version(0, 0));
    }

    /// <summary>
    /// The version and the platform are fixed for the life of the process, so
    /// asking twice would make every plugin pay for the boundary for an answer
    /// that cannot have changed.
    /// </summary>
    [Fact]
    public void AFixedFactIsAskedForOnceAndThenRemembered()
    {
        CountingBroker broker = Answering("\"windows\"");
        RemoteServerInfo info = new(Call(broker));

        _ = info.Platform;
        _ = info.Platform;

        broker.Calls.Should().Be(1);
    }

    /// <summary>
    /// The owner can grant a folder while the plugin is running, and a list
    /// cached at startup would tell the plugin its newest folder does not
    /// exist.
    /// </summary>
    [Fact]
    public void TheGrantedPathsAreAskedForEveryTimeBecauseTheOwnerCanChangeThem()
    {
        CountingBroker broker = Answering("[]");
        RemoteServerInfo info = new(Call(broker));

        _ = info.GrantedPaths;
        _ = info.GrantedPaths;

        broker.Calls.Should().Be(2);
    }

    [Fact]
    public async Task TheFreeSpaceAnswerCrossesBackAsANumber()
    {
        RemoteServerInfo info = new(Call(Answering("4096")));

        (await info.FreeSpaceBytesAsync("movies")).Should().Be(4096);
    }

    /// <summary>
    /// A folder the owner never granted is refused, and the refusal has to
    /// arrive as one. A silent zero would read to a plugin as a full disk.
    /// </summary>
    [Fact]
    public async Task AFolderTheOwnerNeverGrantedIsRefusedRatherThanAnsweredZero()
    {
        CountingBroker broker = new(
            PluginCallResponse.Refused(
                new WireRefusal(
                    PluginRefusalCodes.FileOutsideGrant,
                    PluginId.ToString(),
                    "The plugin reached a path outside every folder it was granted: movies",
                    "The owner has not granted that folder.",
                    "Ask the owner for the folder. Docs: /nomercy-plugins/capabilities/storage",
                    PluginRefusalSeverity.Blocked.ToString()
                )
            )
        );

        RemoteServerInfo info = new(Call(broker));

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            info.FreeSpaceBytesAsync("movies")
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
    }

    private static RemoteCall Call(IPluginBrokerService broker) => new(PluginId, broker);

    private static CountingBroker Answering(string payloadJson) =>
        new(PluginCallResponse.Value(payloadJson));
}

internal sealed class CountingBroker(PluginCallResponse response) : IPluginBrokerService
{
    public int Calls { get; private set; }

    public Task<PluginCallResponse> CallAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        Calls++;

        return Task.FromResult(response);
    }

    public Task<PluginCallResponse> PublishAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => CallAsync(request, context);

    public async IAsyncEnumerable<PluginCallRequest> SubscribeAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        await Task.CompletedTask;
        yield break;
    }
}
