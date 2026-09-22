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

using System.Reflection;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginHostContainerRemovedTests
{
    [Fact]
    public void The_context_no_longer_hands_out_the_host_container()
    {
        typeof(IPluginContext).GetProperty("Services").Should().BeNull();
    }

    [Fact]
    public void The_context_no_longer_hands_out_the_raw_event_bus()
    {
        typeof(IPluginContext).GetProperty("EventBus").Should().BeNull();
    }

    [Fact]
    public void Events_are_reached_through_a_topic_facade()
    {
        PropertyInfo? events = typeof(IPluginContext).GetProperty("Events");

        events.Should().NotBeNull();
        events!.PropertyType.Should().Be(typeof(IPluginEvents));
    }

    [Fact]
    public void A_registrator_registers_into_its_own_collection()
    {
        MethodInfo register = typeof(IPluginServiceRegistrator).GetMethod("RegisterServices")!;

        register.GetParameters().Should().HaveCount(1);
    }

    [Fact]
    public void Asking_for_a_host_service_refuses_and_names_the_facade()
    {
        PluginRefusal refusal = PluginRefusalMessages.HostServicesRemoved(
            "Torrent Downloader 0.4.1",
            "NoMercy.MediaProcessing.Inbox.IInboxMetadataProbe"
        );

        refusal.Code.Should().Be(PluginRefusalCodes.HostServicesRemoved);
        refusal.Fix.Should().Contain("context.Metadata.QueryAsync");
        refusal.Fix.Should().Contain("/nomercy-plugins/capabilities/metadata-query");
    }
}
