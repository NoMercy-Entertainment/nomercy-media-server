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
/// The server's half of the net facade: whether this plugin may reach that
/// host, and nothing more.
/// <para>
/// The socket itself is opened in the plugin's process. What the server owes
/// the owner is the decision, and two different refusals: one for a plugin
/// that may not dial at all, one for a plugin dialing somewhere it was not
/// granted. They have different fixes.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class NetBrokerTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Fact]
    public async Task APluginThatMayNotDialAtAll_IsToldToDeclareTheCapability()
    {
        PluginCallResponse response = await Dial(new FakeCapabilities(Refusal()));

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.SocketUndeclared);
    }

    /// <summary>
    /// The capability is there and the host is not, which is a one-line
    /// manifest change rather than a redesign. Answering the same refusal for
    /// both would send the author to fix something that is already right.
    /// </summary>
    [Fact]
    public async Task APluginDialingAHostItWasNotGranted_IsToldAboutTheHost()
    {
        PluginCallResponse response = await Dial(new ScopedCapabilities(Refusal()));

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.HostNotAllowed);
        response.Refusal.What.Should().Contain("radio.example");
    }

    [Fact]
    public async Task AGrantedHostIsPermittedAndTheServerOpensNothing()
    {
        PluginCallResponse response = await Dial(new FakeCapabilities(null));

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Be("true");
    }

    [Fact]
    public async Task ANetMemberThatDoesNotCrossYet_SaysSoByName()
    {
        PluginCallResponse response = await Ask(new FakeCapabilities(null), "ListenAsync");

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("ListenAsync");
    }

    private static Task<PluginCallResponse> Dial(IPluginCapabilityBroker capabilities) =>
        Ask(capabilities, nameof(IPluginNet.DialAsync), """{"host":"radio.example","port":8000}""");

    private static Task<PluginCallResponse> Ask(
        IPluginCapabilityBroker capabilities,
        string member,
        string payloadJson = "{}"
    ) =>
        new PluginBrokerService(
            PluginId,
            capabilities,
            new RecordingSecrets(),
            new RecordingBinaries(),
            new FakeServerInfo(),
            new FakeStorageRoots(),
            new RecordingLibrary(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingServices(),
            new RecordingSettings(),
            new RecordingUserData()
        ).CallAsync(new PluginCallRequest(PluginId.ToString(), "net", member, payloadJson, null));

    private static PluginRefusal Refusal() =>
        new(
            PluginRefusalCodes.CapabilityNotDeclared,
            "radio",
            "The plugin opened a socket.",
            "It did not declare the network.dial capability.",
            "Declare it in the manifest. Docs: /nomercy-plugins/capabilities/network-dial",
            PluginRefusalSeverity.Blocked
        );
}

/// <summary>
/// Grants the capability itself and refuses every scoped question, which is
/// the plugin that may dial but not there.
/// </summary>
internal sealed class ScopedCapabilities(PluginRefusal refusal) : IPluginCapabilityBroker
{
    public PluginRefusal? Check(Ulid pluginId, string capability, string? scopeValue = null) =>
        scopeValue is null ? null : refusal;
}
