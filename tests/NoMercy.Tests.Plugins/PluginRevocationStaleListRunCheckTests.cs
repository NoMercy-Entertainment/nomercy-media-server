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
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// #65: a server that has not reached nomercy.tv for over a week pauses only the
/// plugins it installed from the marketplace, the ones whose folder holds the package
/// hash the install stored. A sideloaded or self-built plugin has no hash and keeps
/// running. Each case runs the real check a plugin passes before it runs.
/// </summary>
public class PluginRevocationStaleListRunCheckTests : IDisposable
{
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nm-revocation-stale-{Ulid.NewUlid():N}"
    );

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException) { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_stale_list_pauses_a_plugin_installed_from_the_marketplace()
    {
        Decide(daysSinceLastList: 8, Installed(packageHash: Hash))!
            .Code.Should()
            .Be(PluginRefusalCodes.RevocationListStale);
    }

    [Fact]
    public void A_stale_list_leaves_a_sideloaded_plugin_running()
    {
        Decide(daysSinceLastList: 8, Installed(packageHash: null)).Should().BeNull();
    }

    [Fact]
    public void A_fresh_list_lets_both_run()
    {
        Decide(daysSinceLastList: 1, Installed(packageHash: Hash)).Should().BeNull();
        Decide(daysSinceLastList: 1, Installed(packageHash: null)).Should().BeNull();
    }

    private static PluginRefusal? Decide(int daysSinceLastList, PluginInfo plugin)
    {
        StubRevocationStore store = new(new(Now.AddDays(-daysSinceLastList), []));

        return new PluginRevocationRunCheck(
            new PluginRevocationGate(store, new StubClock(Now)),
            store
        ).Check(plugin);
    }

    /// <summary>An installed plugin; a null hash is one that did not come from the marketplace.</summary>
    private PluginInfo Installed(string? packageHash)
    {
        string pluginFolder = Path.Combine(_folder, "plugins", Ulid.NewUlid().ToString());
        Directory.CreateDirectory(pluginFolder);
        File.WriteAllText(Path.Combine(pluginFolder, "plugin.json"), "{}");

        if (packageHash is not null)
            PluginPackageHashMarker.Write(pluginFolder, packageHash);

        return new()
        {
            Id = Ulid.NewUlid(),
            Name = "Sample",
            Description = "A sample plugin.",
            Version = new(1, 0, 0),
            Status = PluginStatus.Active,
            ManifestPath = Path.Combine(pluginFolder, "plugin.json"),
            AssemblyPath = Path.Combine(pluginFolder, "Sample.dll"),
        };
    }

    private sealed class StubRevocationStore(PluginRevocationList list) : IPluginRevocationStore
    {
        public PluginRevocationList Current => list;

        public void Save(PluginRevocationList replacement) { }
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
