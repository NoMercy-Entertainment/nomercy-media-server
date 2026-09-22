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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Verification;
using NoMercy.Tests.Common;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A real load of a real assembly the verifier calls Trusted. Trust used to be
/// enough to start a plugin on its own, so a plugin from an index the owner
/// trusts — and any plugin whose repository published a matching checksum,
/// because that grants trust too — reached the network before the owner was
/// ever asked.
/// </summary>
public class PluginTrustDoesNotConsentTests : IDisposable
{
    private readonly string _pluginsDir;
    private readonly string _echoDir;

    public PluginTrustDoesNotConsentTests()
    {
        _pluginsDir = Path.Combine(Path.GetTempPath(), "nomercy-plugin-trust-" + Ulid.NewUlid());
        _echoDir = Path.Combine(_pluginsDir, "Echo");
        Directory.CreateDirectory(_echoDir);
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (Exception) { }
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class AlwaysTrustedVerifier : IPluginVerifier
    {
        public PluginVerificationResult Verify(
            PluginManifest manifest,
            string assemblyPath,
            string? expectedChecksum,
            string? packagePath = null,
            bool fromMarketplace = false
        ) => new() { Verified = true, Trusted = true };
    }

    [Fact]
    public async Task A_trusted_plugin_that_wants_more_than_the_baseline_loads_disabled()
    {
        StageEcho("""{ "hooks": [], "rest": true }""");

        using PluginManager manager = Manager();
        await manager.LoadAllAsync();

        PluginInfo loaded = manager.GetInstalledPlugins().Single();
        loaded.Trusted.Should().BeTrue();
        loaded.Status.Should().Be(PluginStatus.Disabled);
    }

    [Fact]
    public async Task A_trusted_baseline_plugin_still_starts_on_its_own()
    {
        StageEcho("""{ "hooks": ["metadata"] }""");

        using PluginManager manager = Manager();
        await manager.LoadAllAsync();

        manager.GetInstalledPlugins().Single().Status.Should().Be(PluginStatus.Active);
    }

    private PluginManager Manager() =>
        new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            TestStorageHelper.CreateBackend(),
            new AlwaysTrustedVerifier()
        );

    /// <summary>
    /// The Echo sample, staged the way a real plugin ships, with the capability
    /// block this case is about written into its manifest.
    /// </summary>
    private void StageEcho(string capabilities)
    {
        string binDir = EchoBinDir();

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(_echoDir, Path.GetFileName(file)), overwrite: true);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(_echoDir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(
            Path.Combine(_echoDir, "plugin.json"),
            $$"""
            {
              "id": "01ECH000000000000000000000",
              "name": "Echo",
              "version": "0.1.0",
              "description": "Sample plugin for the test suite",
              "assembly": "NoMercy.Plugin.Samples.Echo.dll",
              "targetAbi": "{{PluginAbi.Current}}",
              "autoEnabled": true,
              "capabilities": {{capabilities}}
            }
            """
        );
    }

    private static string EchoBinDir()
    {
        string testBinDir = Path.GetDirectoryName(
            typeof(PluginTrustDoesNotConsentTests).Assembly.Location
        )!;
        string tfm = Path.GetFileName(testBinDir);
        string configuration = Path.GetFileName(Path.GetDirectoryName(testBinDir)!);

        return Path.Combine(
            RepoPaths.Root,
            "tests",
            "NoMercy.Plugin.Samples.Echo",
            "bin",
            configuration,
            tfm
        );
    }
}
