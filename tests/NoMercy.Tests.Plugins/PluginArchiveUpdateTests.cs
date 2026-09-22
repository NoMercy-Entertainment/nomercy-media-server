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
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.PluginSdk;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Installing over a plugin that is already there.
/// <para>
/// The catalogue's Update button lands here, and it used to unpack straight over
/// the installed folder. Extraction writes one entry at a time, so a failure part
/// way through left the folder holding the new manifest beside the old assembly —
/// and replacing a loaded plugin's own assembly is not a failure that might
/// happen, it is what always happens on Windows, where the file stays locked for
/// as long as the process lives. The owner saw a 500 with a stack trace, and the
/// server was then reporting a version it was not running.
/// </para>
/// <para>
/// What these assert is where the files land and what the installed copy looks
/// like afterwards. The assemblies written here are not real ones; loading is
/// expected to fail and everything under test is decided before the loader is
/// reached.
/// </para>
/// </summary>
public class PluginArchiveUpdateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-update-{Ulid.NewUlid():N}"
    );
    private readonly string _pluginsDir;
    private readonly PluginManager _manager;

    private const string FolderName = "NoMercy.Plugin.InternetRadio";
    private const string AssemblyName = "NoMercy.Plugin.InternetRadio.dll";

    /// <summary>
    /// Spelled out rather than read off PluginManager: this is where an update
    /// waits on disk, so a rename that the boot scan was not told about should
    /// fail here rather than pass by following the constant.
    /// </summary>
    private const string PendingUpdates = ".pending-updates";

    public PluginArchiveUpdateTests()
    {
        _pluginsDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(_pluginsDir);

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

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static string Manifest(string version) =>
        $$"""
        {
          "id": "5KTKRT4Z2Y9P59Y40W5CX4TQKF",
          "name": "Internet Radio",
          "description": "Browse and play internet radio stations in the built-in player.",
          "version": "{{version}}",
          "targetAbi": "12.0",
          "author": "NoMercy Community",
          "assembly": "{{AssemblyName}}"
        }
        """;

    private static void Write(ZipArchive archive, string entryName, string content)
    {
        using StreamWriter writer = new(archive.CreateEntry(entryName).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    /// <summary>An archive of the published shape, at a named version.</summary>
    private string Archive(string version)
    {
        string path = Path.Combine(_tempDir, $"radio-{version}.zip");

        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, $"{FolderName}/plugin.json", Manifest(version));
        Write(archive, $"{FolderName}/{AssemblyName}", $"MZ {version}");
        Write(archive, $"{FolderName}/lang/en.json", $$"""{"version":"{{version}}"}""");

        return path;
    }

    private async Task InstallIgnoringLoad(string archivePath)
    {
        try
        {
            await _manager.InstallPluginArchiveAsync(archivePath, null, CancellationToken.None);
        }
        catch (BadImageFormatException)
        {
            // Everything under test happened before the loader saw the file.
        }
    }

    private string Installed(params string[] parts) =>
        Path.Combine([_pluginsDir, FolderName, .. parts]);

    [Fact]
    public async Task InstallingOverAnEarlierVersion_ReplacesEveryFile()
    {
        await InstallIgnoringLoad(Archive("1.0.0"));
        await InstallIgnoringLoad(Archive("1.2.0"));

        File.ReadAllText(Installed(AssemblyName)).Should().Be("MZ 1.2.0");
        File.ReadAllText(Installed("plugin.json")).Should().Contain("\"version\": \"1.2.0\"");
        File.ReadAllText(Installed("lang", "en.json"))
            .Should()
            .Contain("1.2.0", "a plugin's other files are part of the version, not decoration");
    }

    /// <summary>
    /// The defect this exists for. An archive is unpacked beside the installed
    /// copy and only moved in once all of it is there, so a failure cannot leave
    /// the manifest and the assembly disagreeing about which version is running.
    /// </summary>
    [Fact]
    public async Task UnpackingIsNotDoneOverTheInstalledCopy()
    {
        await InstallIgnoringLoad(Archive("1.0.0"));

        string manifestBefore = File.ReadAllText(Installed("plugin.json"));
        string corrupt = Path.Combine(_tempDir, "corrupt.zip");
        await File.WriteAllTextAsync(corrupt, "this is not a zip");

        Func<Task> act = () =>
            _manager.InstallPluginArchiveAsync(corrupt, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();

        File.ReadAllText(Installed("plugin.json"))
            .Should()
            .Be(manifestBefore, "a failed install must leave the working one exactly as it was");
        File.ReadAllText(Installed(AssemblyName)).Should().Be("MZ 1.0.0");
    }

    [Fact]
    public async Task ASuccessfulInstall_LeavesNothingStagedBehind()
    {
        await InstallIgnoringLoad(Archive("1.0.0"));

        Directory
            .Exists(Path.Combine(_pluginsDir, PendingUpdates))
            .Should()
            .BeFalse("staging is where an update waits, not where it accumulates");
    }

    /// <summary>
    /// The boot scan must not treat the staging folder as a plugin: it holds
    /// versions that are not installed yet, and loading one would run something
    /// the rest of the server does not know about.
    /// </summary>
    [Fact]
    public async Task AStagedUpdate_IsAppliedOnTheNextStartAndNotLoadedFromStaging()
    {
        await InstallIgnoringLoad(Archive("1.0.0"));

        // What a locked assembly leaves behind, written directly because the
        // lock itself cannot be reproduced without loading a real plugin.
        string staged = Path.Combine(
            _pluginsDir,
            PendingUpdates,
            FolderName
        );
        Directory.CreateDirectory(Path.Combine(staged, "lang"));
        await File.WriteAllTextAsync(Path.Combine(staged, "plugin.json"), Manifest("1.2.0"));
        await File.WriteAllTextAsync(Path.Combine(staged, AssemblyName), "MZ 1.2.0");
        await File.WriteAllTextAsync(
            Path.Combine(staged, "lang", "en.json"),
            """{"version":"1.2.0"}"""
        );

        await _manager.LoadPluginsFromDirectoryAsync(CancellationToken.None);

        File.ReadAllText(Installed(AssemblyName))
            .Should()
            .Be("MZ 1.2.0", "the restart is when the files are free to be replaced");
        File.ReadAllText(Installed("plugin.json")).Should().Contain("1.2.0");
        File.ReadAllText(Installed("lang", "en.json")).Should().Contain("1.2.0");
        Directory
            .Exists(staged)
            .Should()
            .BeFalse("an update that has landed is no longer pending");
    }
}
