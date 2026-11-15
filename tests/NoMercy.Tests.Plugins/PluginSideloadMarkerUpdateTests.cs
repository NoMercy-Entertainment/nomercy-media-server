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
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Sideload;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin the owner supplied as a file stays marked as one through an
/// update, whether the update lands now or on the next start (AUD-1017).
/// </summary>
public class PluginSideloadMarkerUpdateTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.Parse("01ECH000000000000000000000");

    private const string AssemblyName = "NoMercy.Plugin.Samples.Echo.dll";
    private const string FolderName = "NoMercy.Plugin.Samples.Echo";

    private readonly string _pluginsDir;
    private readonly string _echoPluginDir;
    private PluginManager _manager;

    public PluginSideloadMarkerUpdateTests()
    {
        _pluginsDir = Path.Combine(Path.GetTempPath(), "nomercy-sideload-update-" + Ulid.NewUlid());
        _echoPluginDir = Path.Combine(_pluginsDir, FolderName);
        Directory.CreateDirectory(_echoPluginDir);
        _manager = NewManager();
    }

    public void Dispose()
    {
        _manager.Dispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_archive_update_from_a_file_leaves_the_plugin_marked(bool markedBefore)
    {
        StageEcho(markedBefore);
        await _manager.LoadPluginsFromDirectoryAsync();

        await UpdateAsync(() => _manager.InstallPluginArchiveAsync(BuildUpdateArchive()));

        File.Exists(Path.Combine(_echoPluginDir, PluginSideloadMarker.FileName)).Should().BeTrue();
        Installed().Version.ToString().Should().Be("2.0.0");
        Installed().Sideloaded.Should().BeTrue();
    }

    [Fact]
    public async Task A_bare_assembly_update_leaves_the_plugin_marked()
    {
        StageEcho(marked: true);
        await _manager.LoadPluginsFromDirectoryAsync();
        Installed().Sideloaded.Should().BeTrue();

        string upload = Path.Combine(_pluginsDir, "upload", AssemblyName);
        Directory.CreateDirectory(Path.GetDirectoryName(upload)!);
        File.Copy(Path.Combine(_echoPluginDir, AssemblyName), upload);

        await UpdateAsync(() => _manager.InstallPluginAsync(upload));

        File.Exists(Path.Combine(_echoPluginDir, PluginSideloadMarker.FileName)).Should().BeTrue();
        Installed().Sideloaded.Should().BeTrue();
    }

    [Fact]
    public async Task A_marketplace_install_of_a_new_plugin_is_not_marked()
    {
        StageEcho(marked: false);
        string archive = BuildUpdateArchive();
        Directory.Delete(_echoPluginDir, recursive: true);

        await _manager.InstallPluginArchiveAsync(archive, fromMarketplace: true);

        File.Exists(Path.Combine(_echoPluginDir, PluginSideloadMarker.FileName)).Should().BeFalse();
        Installed().Sideloaded.Should().BeFalse();
    }

    // An update that could not land now is applied by the next start, so a
    // fresh manager over the same folder stands in for that start.
    private async Task UpdateAsync(Func<Task> update)
    {
        try
        {
            await update();
        }
        catch (PluginUpdatePendingRestartException)
        {
            _manager.Dispose();
            _manager = NewManager();
            await _manager.LoadPluginsFromDirectoryAsync();
        }
    }

    private PluginInfo Installed() =>
        _manager.GetInstalledPlugins().Should().ContainSingle(p => p.Id == PluginId).Which;

    private PluginManager NewManager() =>
        new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            TestStorageHelper.CreateBackend()
        );

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

    private void StageEcho(bool marked)
    {
        string binDir = Path.GetDirectoryName(
            CodeScanVerificationStageTests.SampleDllPath("NoMercy.Plugin.Samples.Echo")
        )!;

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(Path.Combine(_echoPluginDir, "plugin.json"), Manifest("1.0.0"));

        if (marked)
            PluginSideloadMarker.Mark(_echoPluginDir);

        InstalledPluginFolder.Record(_pluginsDir, _echoPluginDir);
    }

    private string BuildUpdateArchive()
    {
        string archivePath = Path.Combine(_pluginsDir, "echo-2.0.0.zip");

        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        foreach (string file in Directory.EnumerateFiles(_echoPluginDir, "*.dll"))
            archive.CreateEntryFromFile(file, $"{FolderName}/{Path.GetFileName(file)}");

        foreach (string file in Directory.EnumerateFiles(_echoPluginDir, "*.deps.json"))
            archive.CreateEntryFromFile(file, $"{FolderName}/{Path.GetFileName(file)}");

        using (StreamWriter writer = new(archive.CreateEntry($"{FolderName}/plugin.json").Open()))
            writer.Write(Manifest("2.0.0"));

        return archivePath;
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
