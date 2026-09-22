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
using Microsoft.AspNetCore.DataProtection;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Uninstalling left a plugin's data folder, its consent, its grants and its
/// secrets on disk, so reinstalling a plugin the owner had thrown away gave it
/// back everything it was allowed without asking anybody.
/// </summary>
public class PluginDataPurgeTests : IDisposable
{
    private static readonly Ulid PluginA = Ulid.Parse("0H248H248H248H248H248H248H");
    private static readonly Ulid PluginB = Ulid.Parse("1248H248H248H248H248H248H2");

    private readonly string _pluginsPath;
    private readonly IStorage _storage;
    private readonly IPluginConfiguration _platform;
    private readonly IPluginConsentService _consent;
    private readonly IPluginGrantStore _grants;
    private readonly PluginDataPurge _purge;

    public PluginDataPurgeTests()
    {
        _pluginsPath = Path.Combine(Path.GetTempPath(), "nomercy-plugin-purge-" + Ulid.NewUlid());
        Directory.CreateDirectory(_pluginsPath);

        _storage = TestStorageHelper.CreateStorage(_pluginsPath);
        _platform = new PluginConfiguration(
            Path.Combine(_pluginsPath, "data", "platform"),
            _storage
        );
        _consent = new PluginConsentService(new ConfigPluginConsentStore(_platform));
        _grants = new ConfigPluginGrantStore(_platform);
        _purge = new(_pluginsPath, _storage, _consent, _grants, _platform);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_pluginsPath))
                Directory.Delete(_pluginsPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string DataFolder(Ulid pluginId) =>
        Path.Combine(_pluginsPath, "data", pluginId.ToString());

    private void WriteDataFile(Ulid pluginId)
    {
        Directory.CreateDirectory(DataFolder(pluginId));
        File.WriteAllText(Path.Combine(DataFolder(pluginId), "state.json"), "{}");
    }

    private PluginSecretStore Secrets(Ulid pluginId) =>
        new(pluginId, new EphemeralDataProtectionProvider(), _platform);

    [Fact]
    public async Task Purging_deletes_the_plugins_own_data_folder()
    {
        WriteDataFile(PluginA);

        await _purge.PurgeAsync(PluginA);

        Directory.Exists(DataFolder(PluginA)).Should().BeFalse();
    }

    [Fact]
    public async Task Purging_leaves_another_plugins_data_folder_alone()
    {
        WriteDataFile(PluginA);
        WriteDataFile(PluginB);

        await _purge.PurgeAsync(PluginA);

        Directory.Exists(DataFolder(PluginB)).Should().BeTrue();
    }

    [Fact]
    public async Task Purging_withdraws_consent_so_a_reinstall_has_to_ask_again()
    {
        _consent.GrantConsent(PluginA, new() { Rest = true }, new(1, 0, 0));

        await _purge.PurgeAsync(PluginA);

        _consent.HasConsent(PluginA).Should().BeFalse();
    }

    [Fact]
    public async Task Purging_takes_away_every_grant()
    {
        _grants.Grant(PluginA, PluginGrantKind.NetworkHost, "tracker.example");
        _grants.Grant(PluginA, PluginGrantKind.PlayerSource, "ice1.somafm.com");

        await _purge.PurgeAsync(PluginA);

        _grants.Granted(PluginA, PluginGrantKind.NetworkHost).Should().BeEmpty();
        _grants.Granted(PluginA, PluginGrantKind.PlayerSource).Should().BeEmpty();
    }

    [Fact]
    public async Task Purging_deletes_the_plugins_secrets_and_no_one_elses()
    {
        await Secrets(PluginA).SetAsync("api-key", "hunter2");
        await Secrets(PluginB).SetAsync("api-key", "still-mine");

        await _purge.PurgeAsync(PluginA);

        (await Secrets(PluginA).KeysAsync()).Should().BeEmpty();
        (await Secrets(PluginB).KeysAsync()).Should().Contain("api-key");
    }
}
