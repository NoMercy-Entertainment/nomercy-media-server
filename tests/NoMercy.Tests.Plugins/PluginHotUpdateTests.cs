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

using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Updates a genuinely resident plugin — the real Echo sample, actually
/// loaded into its own AssemblyLoadContext — end to end through
/// <see cref="PluginManager.InstallPluginArchiveAsync"/>.
/// <para>
/// Every other update test in this project (<c>PluginArchiveUpdateTests</c>)
/// uses a stub assembly precisely so loading fails and the plugin is never
/// actually resident — that is what lets those tests stay fast and
/// deterministic, but it also means the "unload a live plugin and swap it"
/// branch this feature exists for has never been exercised by a real load
/// context. This class is the one that does.
/// </para>
/// <para>
/// Whether the swap completes hot or falls back to staging for the next start
/// depends on how quickly this process's GC actually collects the just-unloaded
/// context — production deliberately never forces that collection (see
/// <see cref="PluginFileRetry"/>), so it is not something a test can pin
/// without lying about what production does. What is provable, and what these
/// assert, is the outcome that actually matters to an owner: the update always
/// converges to the new version, and nothing is ever destroyed on the way —
/// exactly once, regardless of which branch the OS happens to take today.
/// </para>
/// </summary>
public class PluginHotUpdateTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.Parse("01ECH000000000000000000000");
    private const string FolderName = "Echo";
    private const string AssemblyName = "NoMercy.Plugin.Samples.Echo.dll";

    private readonly string _pluginsDir;
    private readonly string _echoPluginDir;
    private readonly PluginManager _manager;

    public PluginHotUpdateTests()
    {
        _pluginsDir = Path.Combine(Path.GetTempPath(), "nomercy-hot-update-" + Ulid.NewUlid());
        _echoPluginDir = Path.Combine(_pluginsDir, FolderName);
        Directory.CreateDirectory(_echoPluginDir);

        _manager = new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            TestStorageHelper.CreateBackend()
        );
    }

    public void Dispose()
    {
        _manager.Dispose();

        // Only to let a short-lived test process clean up its own temp
        // directory promptly — production never does this (see
        // PluginFileRetry's own doc comment on why not).
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (Exception) { }
    }

    private static string EchoBinDir()
    {
        string testBinDir = Path.GetDirectoryName(typeof(PluginHotUpdateTests).Assembly.Location)!;
        string buildConfig = Path.GetFileName(Path.GetDirectoryName(testBinDir)!);
        string repoRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", ".."));

        string preferred = Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.Echo",
            "bin",
            buildConfig,
            "net10.0"
        );

        if (Directory.Exists(preferred))
            return preferred;

        return Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.Echo",
            "bin",
            string.Equals(buildConfig, "Release", StringComparison.OrdinalIgnoreCase)
                ? "Debug"
                : "Release",
            "net10.0"
        );
    }

    private static string Manifest(string version) =>
        $$"""
            {
              "id": "{{PluginId}}",
              "name": "Echo",
              "version": "{{version}}",
              "description": "Sample plugin for the test suite",
              "assembly": "{{AssemblyName}}",
              "autoEnabled": true
            }
            """;

    private void StageEchoV1()
    {
        string binDir = EchoBinDir();
        string dllSrc = Path.Combine(binDir, AssemblyName);

        if (!File.Exists(dllSrc))
            throw new FileNotFoundException(
                $"Echo plugin DLL not found at '{dllSrc}'. Build NoMercy.Plugin.Samples.Echo first."
            );

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(Path.Combine(_echoPluginDir, "plugin.json"), Manifest("1.0.0"));
    }

    /// <summary>
    /// The v2 update archive: every file the v1 install has, republished as an
    /// archive with a bumped manifest version. The assembly bytes are
    /// identical — a real update need not change code to prove the swap
    /// mechanics, and using the same real, loadable DLL is what makes this a
    /// genuine residency case rather than another stub.
    /// </summary>
    private string BuildV2Archive()
    {
        string archivePath = Path.Combine(_pluginsDir, "echo-2.0.0.zip");

        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        foreach (string file in Directory.EnumerateFiles(_echoPluginDir))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
                continue;

            archive.CreateEntryFromFile(file, $"{FolderName}/{name}");
        }

        using (StreamWriter writer = new(archive.CreateEntry($"{FolderName}/plugin.json").Open()))
            writer.Write(Manifest("2.0.0"));

        return archivePath;
    }

    [Fact]
    public async Task UpdatingAResidentPlugin_AlwaysConvergesOnTheNewVersion()
    {
        StageEchoV1();
        await _manager.LoadPluginsFromDirectoryAsync();

        _manager
            .GetInstalledPlugins()
            .Should()
            .ContainSingle(p => p.Id == PluginId)
            .Which.Version.ToString()
            .Should()
            .Be("1.0.0");

        string archivePath = BuildV2Archive();

        try
        {
            await _manager.InstallPluginArchiveAsync(archivePath);
        }
        catch (PluginUpdatePendingRestartException)
        {
            // The fallback this feature keeps as a safety net: the assembly did
            // not free up inside the wait budget on this run, so the update is
            // staged exactly as it always was — and the very next load applies
            // it, same as a restart would have.
            await _manager.LoadPluginsFromDirectoryAsync();
        }

        PluginInfo updated = _manager
            .GetInstalledPlugins()
            .Should()
            .ContainSingle(
                p => p.Id == PluginId,
                "the update must never duplicate or drop the plugin"
            )
            .Which;

        updated.Version.ToString().Should().Be("2.0.0");
        updated
            .Status.Should()
            .Be(
                PluginStatus.Active,
                "an update must not leave the plugin worse off than before it started"
            );
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
