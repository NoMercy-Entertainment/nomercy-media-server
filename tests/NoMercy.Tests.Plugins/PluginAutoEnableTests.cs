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
/// Which plugins start running on their own, and which wait for the owner.
/// <para>
/// A verification that came back Trusted used to be enough on its own, so a
/// plugin from an index the owner trusts - and, because a matching checksum
/// also grants trust, any plugin whose repository published one - started
/// reaching the network and the claims pipeline without the owner ever being
/// asked. Trust says where a plugin came from. It does not answer for the
/// owner.
/// </para>
/// </summary>
public class PluginAutoEnableTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private static PluginManifest Manifest(
        PluginCapabilities? capabilities,
        bool autoEnabled = true
    ) =>
        new()
        {
            Id = new PluginId(PluginId),
            Name = "Torrent Downloader",
            Description = "d",
            Version = "0.5.0",
            Assembly = "NoMercy.Plugin.Torrent.dll",
            AutoEnabled = autoEnabled,
            Capabilities = capabilities,
        };

    private static IPluginConsentService Consent() =>
        new PluginConsentService(new InMemoryConsentStore());

    [Fact]
    public void A_baseline_plugin_starts_on_its_own()
    {
        PluginAutoEnable
            .Allows(Manifest(new() { Hooks = [PluginHookCapability.Metadata] }), Consent())
            .Should()
            .BeTrue();
    }

    [Fact]
    public void An_elevated_plugin_waits_for_the_owner()
    {
        PluginAutoEnable.Allows(Manifest(new() { Rest = true }), Consent()).Should().BeFalse();
    }

    [Fact]
    public void A_plugin_the_owner_consented_to_starts()
    {
        IPluginConsentService consent = Consent();
        PluginCapabilities capabilities = new() { Rest = true };
        consent.GrantConsent(PluginId, capabilities, new(0, 5, 0));

        PluginAutoEnable.Allows(Manifest(capabilities), consent).Should().BeTrue();
    }

    [Fact]
    public void A_plugin_that_asked_not_to_auto_enable_is_left_alone()
    {
        PluginAutoEnable
            .Allows(
                Manifest(new() { Hooks = [PluginHookCapability.Metadata] }, autoEnabled: false),
                Consent()
            )
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// A manifest that asks for more than the owner approved is a new question,
    /// whatever the plugin's provenance says.
    /// </summary>
    [Fact]
    public void A_plugin_that_widened_asks_again()
    {
        IPluginConsentService consent = Consent();
        consent.GrantConsent(PluginId, new() { Rest = true }, new(0, 4, 0));

        PluginAutoEnable
            .NeedsReConsent(Manifest(new() { Rest = true, Ws = true }), consent)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void A_plugin_nobody_ever_consented_to_is_not_asking_again()
    {
        PluginAutoEnable
            .NeedsReConsent(Manifest(new() { Rest = true }), Consent())
            .Should()
            .BeFalse();
    }
}
