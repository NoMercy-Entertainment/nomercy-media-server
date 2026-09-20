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
using NoMercy.Plugins.Testing;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The harness has to refuse for the same reasons the host refuses. One that
/// said yes to everything would let a plugin pass its own tests and then fail
/// on the first server it met, which is worse than having no harness.
/// </summary>
public class PluginTestingHarnessTests
{
    [Fact]
    public async Task A_call_without_the_grant_refuses_and_the_recorder_keeps_it()
    {
        FakePluginContext context = new("Torrent Downloader 1.0.0");

        Func<Task> dial = () => context.Net.DialAsync("tracker.example", 443, PluginTransport.Tcp);

        await dial.Should().ThrowAsync<PluginRefusedException>();
        context
            .Refusals.Should()
            .ContainSingle()
            .Which.Code.Should()
            .Be(PluginRefusalCodes.CapabilityNotDeclared);
    }

    [Fact]
    public async Task A_call_with_the_grant_goes_through()
    {
        FakePluginContext context = new("Torrent Downloader 1.0.0");
        context.Grant(PluginCapabilityNames.NetworkDial, "tracker.example");

        Stream stream = await context.Net.DialAsync("tracker.example", 443, PluginTransport.Tcp);

        stream.Should().NotBeNull();
        context.Refusals.Should().BeEmpty();
    }

    [Fact]
    public async Task A_grant_with_the_wrong_scope_refuses_with_the_scope_code()
    {
        FakePluginContext context = new("Torrent Downloader 1.0.0");
        context.Grant(PluginCapabilityNames.NetworkDial, "tracker.example");

        Func<Task> dial = () => context.Net.DialAsync("other.example", 443, PluginTransport.Tcp);

        await dial.Should().ThrowAsync<PluginRefusedException>();
        context
            .Refusals.Last()
            .Code.Should()
            .Be(
                PluginRefusalCodes.CapabilityScopeRefused,
                "declared-but-not-for-this is a different fix from not declared at all"
            );
    }

    [Fact]
    public async Task A_capability_granted_with_no_scope_covers_every_host()
    {
        FakePluginContext context = new("Torrent Downloader 1.0.0");
        context.Grant(PluginCapabilityNames.NetworkDial);

        await context.Net.DialAsync("anything.example", 443, PluginTransport.Tcp);

        context.Refusals.Should().BeEmpty();
    }

    [Fact]
    public async Task Revoking_takes_it_away_again()
    {
        FakePluginContext context = new("Torrent Downloader 1.0.0");
        context.Grant(PluginCapabilityNames.NetworkDial);
        context.Revoke(PluginCapabilityNames.NetworkDial);

        Func<Task> dial = () => context.Net.DialAsync("anything.example", 443, PluginTransport.Tcp);

        await dial.Should().ThrowAsync<PluginRefusedException>();
    }

    [Fact]
    public async Task What_the_plugin_dialed_is_recorded_so_a_test_can_name_it()
    {
        FakePluginContext context = new("Torrent Downloader 1.0.0");
        context.Grant(PluginCapabilityNames.NetworkDial);
        FakePluginNet net = (FakePluginNet)context.Net;

        await net.DialAsync("first.example", 443, PluginTransport.Tcp);
        await net.DialAsync("second.example", 80, PluginTransport.Tcp);

        net.Dialed.Should()
            .Equal(
                ["first.example:443", "second.example:80"],
                "the order matters when a plugin falls through a mirror list"
            );
    }

    [Fact]
    public async Task An_event_a_plugin_published_is_recorded()
    {
        FakePluginContext context = new("Torrent Downloader 1.0.0");

        await context.Events.PublishAsync("transfer.finished", new { });

        context
            .Published.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                "transfer.finished",
                "an event nothing publishes looks exactly like one nothing subscribed to"
            );
    }

    [Fact]
    public void A_view_snapshot_is_stable_json_a_test_can_assert_on()
    {
        PluginView view = PluginViews.Declarative(PluginViews.Text("hello", "radio.hello"));

        string snapshot = PluginViewSnapshot.Of(view);

        snapshot.Should().Contain("radio.hello");
        PluginViewSnapshot
            .Of(view)
            .Should()
            .Be(snapshot, "a snapshot that differs must differ because the view changed");
    }
}
