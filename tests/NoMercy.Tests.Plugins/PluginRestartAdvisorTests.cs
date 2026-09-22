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
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Telling an owner they do NOT need to restart is the point. Nothing said
/// either way before, so every plugin action read as though it might, and an
/// owner who cannot tell restarts after all of them — on a server other people
/// are watching things on.
/// </summary>
public class PluginRestartAdvisorTests
{
    private static readonly Ulid PluginId = Ulid.Parse("3QEXVQEXVQEXVQEXVQEXVQEXVQ");

    private static PluginInfo Plugin(
        bool contributesServices = false,
        bool rest = false,
        Ulid? id = null
    ) =>
        new()
        {
            Id = id ?? PluginId,
            Name = "Probe",
            Description = "",
            Version = new(1, 0, 0),
            Status = PluginStatus.Active,
            ContributesServices = contributesServices,
            Capabilities = new() { Rest = rest },
        };

    [Fact]
    public void An_ordinary_plugin_enables_with_no_restart()
    {
        PluginRestartAdvisor advisor = new();

        PluginRestartRequirement requirement = advisor.Evaluate(Plugin(), PluginOperation.Enable);

        requirement.Required.Should().BeFalse();
        requirement.Explain().Should().BeEmpty();
    }

    [Fact]
    public void Disabling_never_needs_a_restart()
    {
        // Even for a plugin that does need one to enable. Dispose runs and the
        // plugin stops acting; its leftover service registrations are untidy,
        // not a reason to stop a server mid-playback.
        PluginRestartAdvisor advisor = new();

        advisor
            .Evaluate(Plugin(contributesServices: true, rest: true), PluginOperation.Disable)
            .Required.Should()
            .BeFalse();
    }

    [Fact]
    public void A_plugin_that_registers_services_needs_one_to_enable()
    {
        PluginRestartAdvisor advisor = new();

        PluginRestartRequirement requirement = advisor.Evaluate(
            Plugin(contributesServices: true),
            PluginOperation.Enable
        );

        requirement.Reasons.Should().HaveFlag(PluginRestartReason.ContributesServices);
        requirement.Explain().Should().ContainSingle();
    }

    [Fact]
    public void A_plugin_that_owns_routes_needs_one_to_enable() =>
        new PluginRestartAdvisor()
            .Evaluate(Plugin(rest: true), PluginOperation.Enable)
            .Reasons.Should()
            .HaveFlag(PluginRestartReason.OwnsRoutes);

    [Fact]
    public void A_plugin_already_registered_at_startup_needs_no_restart_to_enable()
    {
        // It was present when the container was built, so its services and
        // routes are already there. Toggling it is live.
        PluginRestartAdvisor advisor = new();
        advisor.MarkRegisteredAtStartup(PluginId);

        advisor
            .Evaluate(Plugin(contributesServices: true, rest: true), PluginOperation.Enable)
            .Required.Should()
            .BeFalse();
    }

    [Fact]
    public void Being_registered_at_startup_does_not_speak_for_another_plugin()
    {
        PluginRestartAdvisor advisor = new();
        advisor.MarkRegisteredAtStartup(PluginId);

        Ulid other = Ulid.Parse("48H248H248H248H248H248H248");

        advisor
            .Evaluate(Plugin(contributesServices: true, id: other), PluginOperation.Enable)
            .Required.Should()
            .BeTrue();
    }

    [Theory]
    [InlineData(PluginOperation.Uninstall)]
    [InlineData(PluginOperation.Update)]
    public void Removing_or_replacing_reports_the_file_lock(PluginOperation operation) =>
        new PluginRestartAdvisor()
            .Evaluate(Plugin(), operation)
            .Reasons.Should()
            .HaveFlag(PluginRestartReason.AssemblyStillLoaded);

    [Fact]
    public void Several_reasons_are_all_explained()
    {
        PluginRestartRequirement requirement = new PluginRestartAdvisor().Evaluate(
            Plugin(contributesServices: true, rest: true),
            PluginOperation.Install
        );

        requirement.Explain().Should().HaveCount(2);
    }

    [Fact]
    public void Nothing_required_explains_nothing() =>
        PluginRestartRequirement.None.Explain().Should().BeEmpty();
}
