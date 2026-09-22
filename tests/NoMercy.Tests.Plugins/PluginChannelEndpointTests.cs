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
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Ipc;
using Xunit;

namespace NoMercy.Tests.Plugins;

[Trait("Category", "Unit")]
public class PluginChannelEndpointTests
{
    private static readonly Ulid PluginId = Ulid.Parse("01J9ZK5V8Y0000000000000000");

    [Fact]
    public void BrokerAndHost_AreDifferentEndpointsForTheSamePlugin()
    {
        PluginChannelEndpoints
            .BrokerFor(PluginId)
            .Should()
            .NotBe(PluginChannelEndpoints.HostFor(PluginId));
    }

    [Fact]
    public void Endpoints_AreScopedToOnePlugin()
    {
        Ulid other = Ulid.NewUlid();

        PluginChannelEndpoints
            .BrokerFor(PluginId)
            .Should()
            .NotBe(PluginChannelEndpoints.BrokerFor(other));
    }

    [SkippableFact]
    public void OnWindows_TheEndpointIsAPipeNameCarryingThePluginId()
    {
        Skip.IfNot(Software.IsWindows);

        PluginChannelEndpoints.BrokerFor(PluginId).Should().Be($"NoMercy.Plugin.{PluginId}.broker");
    }

    [SkippableFact]
    public void OnUnix_TheEndpointIsASocketUnderThePluginRunDirectory()
    {
        Skip.If(Software.IsWindows);

        PluginChannelEndpoints
            .BrokerFor(PluginId)
            .Should()
            .Be(Path.Combine(AppFiles.PluginsPath, "run", PluginId.ToString(), "broker.sock"));
    }

    [Fact]
    public void ARefusalCrossesTheWireWithEveryTeachingField()
    {
        WireRefusal refusal = new(
            "PLUGIN_HOST_UNAVAILABLE",
            "Internet Radio 2.0.0",
            "The server could not reach the plugin's process.",
            "The plugin process is starting, restarting after a crash, or was disabled.",
            "Open the plugin's health page and press Restart, or check the server log for PLUGIN_HOST_CRASHED. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            "blocked"
        );

        PluginCallResponse response = PluginCallResponse.Refused(refusal);

        response.Ok.Should().BeFalse();
        response.Refusal!.Fix.Should().Contain("/nomercy-plugins/");
    }
}
