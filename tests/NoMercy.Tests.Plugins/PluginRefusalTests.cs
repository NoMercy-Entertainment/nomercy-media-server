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
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginRefusalTests
{
    [Fact]
    public void Every_code_is_declared_once()
    {
        PluginRefusalCodes.All.Select(refusal => refusal.Code)
            .Should()
            .OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_code_has_a_severity_and_a_summary()
    {
        foreach (PluginRefusalDescriptor descriptor in PluginRefusalCodes.All)
        {
            descriptor.Summary.Should().NotBeNullOrWhiteSpace(descriptor.Code);
        }
    }

    [Fact]
    public void A_refusal_serializes_in_the_shape_the_design_states()
    {
        PluginRefusal refusal = new(
            PluginRefusalCodes.CapabilityNotDeclared,
            "Torrent Downloader 0.4.1",
            "The plugin opened a TCP socket to eztv.re:443.",
            "Raw sockets need the capability network.dial. The manifest does not declare it.",
            "Add { \"name\": \"network.dial\", \"scope\": \"eztv.re\", \"reason\": \"...\" } to plugin.json, or use context.Http for HTTP requests. Docs: /nomercy-plugins/capabilities/network-dial",
            PluginRefusalSeverity.Blocked
        );

        string json = JsonSerializer.Serialize(refusal);

        json.Should().Contain("\"code\":\"PLUGIN_CAPABILITY_NOT_DECLARED\"");
        json.Should().Contain("\"plugin\":");
        json.Should().Contain("\"what\":");
        json.Should().Contain("\"why\":");
        json.Should().Contain("\"fix\":");
        json.Should().Contain("\"severity\":\"blocked\"");
    }

    [Fact]
    public void Throwing_a_refusal_carries_it_intact()
    {
        PluginRefusal refusal = new(
            PluginRefusalCodes.ProcessSpawnUndeclared,
            "Torrent Downloader 0.4.1",
            "The plugin started Xvfb.",
            "Starting a process needs the capability process.spawn. The manifest does not declare it.",
            "Declare process.spawn with scope Xvfb in plugin.json; this needs High publisher trust. Docs: /nomercy-plugins/capabilities/process-spawn",
            PluginRefusalSeverity.Blocked
        );

        PluginRefusedException thrown = new(refusal);

        thrown.Refusal.Should().Be(refusal);
        thrown.Message.Should().Be(refusal.What);
    }

    [Fact]
    public void Every_code_that_names_a_capability_names_a_real_one()
    {
        foreach (PluginRefusalDescriptor descriptor in PluginRefusalCodes.All.Where(refusal => refusal.Capability is not null))
        {
            PluginCapabilityVocabulary.ByName(descriptor.Capability!)
                .Should()
                .NotBeNull(descriptor.Code);
        }
    }
}
