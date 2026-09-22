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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The owner used to have two answers: everything, or nothing and the plugin
/// does not run. Someone who wanted a radio plugin to reach the internet and
/// not to spawn processes had no way to say so.
/// </summary>
public class PluginPerCapabilityConsentTests
{
    private static readonly Ulid PluginUlid = Ulid.Parse("01J9ZK5V8Y0000000000000000");

    private static PluginConsentService Service() => new(new InMemoryConsentStore());

    [Fact]
    public void Approving_one_capability_leaves_the_others_pending()
    {
        PluginConsentService service = Service();

        service.ApproveCapability(PluginUlid, "network.fetch", new(1, 0, 0));

        service.IsApproved(PluginUlid, "network.fetch").Should().BeTrue();
        service.IsApproved(PluginUlid, "process.spawn").Should().BeFalse();
    }

    [Fact]
    public void Revoking_one_capability_keeps_the_rest()
    {
        PluginConsentService service = Service();
        service.ApproveCapability(PluginUlid, "network.fetch", new(1, 0, 0));
        service.ApproveCapability(PluginUlid, "library.read", new(1, 0, 0));

        service.RevokeCapability(PluginUlid, "network.fetch");

        service.IsApproved(PluginUlid, "network.fetch").Should().BeFalse();
        service.IsApproved(PluginUlid, "library.read").Should().BeTrue();
    }

    [Fact]
    public void Approval_records_the_version_that_asked()
    {
        PluginConsentService service = Service();

        service.ApproveCapability(PluginUlid, "network.fetch", new(2, 1, 0));

        service.ApprovedAt(PluginUlid, "network.fetch").Should().Be(new Version(2, 1, 0));
        service
            .ApprovedAt(PluginUlid, "process.spawn")
            .Should()
            .BeNull("never approved is not the same as approved at version zero");
    }

    [Fact]
    public void Revoking_something_never_approved_changes_nothing()
    {
        PluginConsentService service = Service();
        service.ApproveCapability(PluginUlid, "network.fetch", new(1, 0, 0));

        service.RevokeCapability(PluginUlid, "process.spawn");

        service.IsApproved(PluginUlid, "network.fetch").Should().BeTrue();
    }

    [Fact]
    public void Approving_again_at_a_newer_version_records_the_newer_one()
    {
        PluginConsentService service = Service();
        service.ApproveCapability(PluginUlid, "network.fetch", new(1, 0, 0));

        service.ApproveCapability(PluginUlid, "network.fetch", new(2, 0, 0));

        service.ApprovedAt(PluginUlid, "network.fetch").Should().Be(new Version(2, 0, 0));
    }

    [Fact]
    public void Updating_the_manifest_does_not_re_approve_what_the_owner_refused()
    {
        InMemoryConsentStore store = new();
        PluginConsentService service = new(store);
        service.ApproveCapability(PluginUlid, "network.fetch", new(1, 0, 0));

        // What an update does: the plugin's declared set is written again.
        store.Add(PluginUlid, new() { Hooks = ["network.fetch", "process.spawn"] }, new(2, 0, 0));

        service.IsApproved(PluginUlid, "network.fetch").Should().BeTrue();
        service
            .IsApproved(PluginUlid, "process.spawn")
            .Should()
            .BeFalse(
                "an update that re-approves what was refused is an update nobody consented to"
            );
    }

    [Fact]
    public void One_plugins_approval_is_not_anothers()
    {
        PluginConsentService service = Service();
        Ulid other = Ulid.Parse("01J9ZK5V8Y0000000000000001");

        service.ApproveCapability(PluginUlid, "network.fetch", new(1, 0, 0));

        service.IsApproved(other, "network.fetch").Should().BeFalse();
    }
}
