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
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Design section 9: every capability the installed manifest declared is
/// marked consented at the version that was installed, and nothing the owner
/// already approved is asked again.
/// <para>
/// A record from before consent tracked capabilities says only "this id was
/// approved". Compared against a manifest that declares rest, ws or a network
/// host, an empty capability set reads as a widening, so every elevated
/// plugin the owner had already said yes to came back Disabled on the first
/// start after the upgrade.
/// </para>
/// </summary>
public class PluginConsentUpgradeTests : IDisposable
{
    private static readonly Guid PreUpgradeGuid = Guid.Parse(
        "395df423-3e2f-4a1c-bc5b-dbc41a9133ef"
    );
    private static readonly Ulid PluginId = new(PreUpgradeGuid);

    private readonly string _tempDir;

    public PluginConsentUpgradeTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-consent-upgrade-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }

        GC.SuppressFinalize(this);
    }

    private string ConfigPath => Path.Combine(_tempDir, "config.json");

    private ConfigPluginConsentStore MakeStore() =>
        new(new PluginConfiguration(_tempDir, TestStorageHelper.CreateStorage(_tempDir)));

    private void WritePreUpgradeRecord() =>
        File.WriteAllText(ConfigPath, $@"{{""GrantedPluginIds"":[""{PreUpgradeGuid}""]}}");

    private static PluginManifest Manifest(PluginCapabilities capabilities, string version) =>
        new()
        {
            Id = new(PluginId),
            Name = "Internet Radio",
            Description = "d",
            Version = version,
            Assembly = "NoMercy.Plugin.InternetRadio.dll",
            AutoEnabled = true,
            Capabilities = capabilities,
        };

    private static PluginCapabilities Installed() =>
        new()
        {
            Rest = true,
            Network = new() { Hosts = ["radio-browser.info"] },
        };

    [Fact]
    public void A_pre_upgrade_consent_still_starts_the_plugin_the_owner_approved()
    {
        WritePreUpgradeRecord();
        PluginConsentService service = new(MakeStore());

        PluginAutoEnable
            .Allows(Manifest(Installed(), "1.4.0"), service)
            .Should()
            .BeTrue("the owner already approved everything this manifest declares");
    }

    [Fact]
    public void A_pre_upgrade_consent_is_not_reported_as_waiting_on_the_owner()
    {
        WritePreUpgradeRecord();
        PluginConsentService service = new(MakeStore());

        PluginAutoEnable.NeedsReConsent(Manifest(Installed(), "1.4.0"), service).Should().BeFalse();
    }

    [Fact]
    public void Reading_a_pre_upgrade_consent_upgrades_it_and_drops_the_old_id_list()
    {
        WritePreUpgradeRecord();
        PluginConsentService service = new(MakeStore());

        service.ConsentCoversCapabilities(PluginId, Installed(), new Version(1, 4, 0));

        PluginConsentGrant? upgraded = MakeStore().Get(PluginId);
        upgraded.Should().NotBeNull();
        upgraded!.PredatesCapabilityTracking.Should().BeFalse();
        upgraded.Capabilities!.Rest.Should().BeTrue();
        upgraded.Capabilities.Network!.Hosts.Should().Contain("radio-browser.info");
        upgraded.ManifestVersion.Should().Be("1.4.0");

        File.ReadAllText(ConfigPath)
            .Should()
            .NotContain(
                PreUpgradeGuid.ToString(),
                "the pre-upgrade id is replaced, not kept beside the row"
            );
    }

    [Fact]
    public void An_update_that_widens_past_the_upgraded_record_still_asks_again()
    {
        WritePreUpgradeRecord();
        PluginConsentService service = new(MakeStore());

        service.ConsentCoversCapabilities(PluginId, Installed(), new Version(1, 4, 0));

        PluginCapabilities widened = new()
        {
            Rest = true,
            Ws = true,
            Network = new() { Hosts = ["radio-browser.info", "tracker.example"] },
        };

        PluginConsentService afterRestart = new(MakeStore());

        PluginAutoEnable.Allows(Manifest(widened, "1.5.0"), afterRestart).Should().BeFalse();
        PluginAutoEnable.NeedsReConsent(Manifest(widened, "1.5.0"), afterRestart).Should().BeTrue();
    }

    [Fact]
    public void A_plugin_with_no_consent_at_all_is_still_refused()
    {
        PluginConsentService service = new(MakeStore());

        PluginAutoEnable.Allows(Manifest(Installed(), "1.4.0"), service).Should().BeFalse();
    }
}
