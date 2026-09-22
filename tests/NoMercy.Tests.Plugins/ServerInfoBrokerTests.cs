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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Ipc;
using NoMercy.Plugins.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The server's half of the server-info facade.
/// <para>
/// No capability gate: the version and the platform are facts about software
/// the owner installed, and the granted paths are filtered by the grants
/// already. What matters here is that a refusal raised deeper down still
/// reaches the plugin as a refusal.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ServerInfoBrokerTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Fact]
    public async Task ThePlatformCrossesBackAsTheServerStatesIt()
    {
        PluginCallResponse response = await Ask(
            new FakeServerInfo(platform: "linux"),
            nameof(IPluginServerInfo.Platform)
        );

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Be("\"linux\"");
    }

    /// <summary>
    /// A folder the owner never granted is refused deep inside the facade,
    /// after the broker has already let the call through. Thrown across the
    /// channel that would arrive as a server that stopped answering.
    /// </summary>
    [Fact]
    public async Task ARefusalRaisedInsideTheFacadeStillCrossesAsARefusal()
    {
        PluginCallResponse response = await Ask(
            new FakeServerInfo(refuseFreeSpace: true),
            nameof(IPluginServerInfo.FreeSpaceBytesAsync),
            """{"folderId":"movies"}"""
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
    }

    [Fact]
    public async Task AServerMemberTheFacadeDoesNotHave_IsRefusedByName()
    {
        PluginCallResponse response = await Ask(new FakeServerInfo(), "ShutDown");

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("ShutDown");
    }

    private static Task<PluginCallResponse> Ask(
        IPluginServerInfo server,
        string member,
        string payloadJson = "{}"
    ) =>
        new PluginBrokerService(
            PluginId,
            new FakeCapabilities(null),
            new RecordingSecrets(),
            new RecordingBinaries(),
            server
        ).CallAsync(
            new PluginCallRequest(PluginId.ToString(), "server", member, payloadJson, null)
        );
}

internal sealed class FakeServerInfo(string platform = "windows", bool refuseFreeSpace = false)
    : IPluginServerInfo
{
    public Version Version => new(11, 4, 2);

    public string Platform => platform;

    public IReadOnlyList<PluginStorageLocation> GrantedPaths => [];

    public Task<long> FreeSpaceBytesAsync(string folderId, CancellationToken ct = default)
    {
        if (refuseFreeSpace)
            throw new PluginRefusedException(
                PluginRefusalMessages.FileOutsideGrant("radio", folderId)
            );

        return Task.FromResult(4096L);
    }
}
