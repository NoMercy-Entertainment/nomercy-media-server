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
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Archive install, shaped after what a plugin is actually published as.
/// <para>
/// The artifact this was written against is a zip holding one folder with the
/// plugin's README, its assembly and its plugin.json. The bare-assembly install
/// path copies a single file, so it drops the manifest and every dependency and
/// could never have installed that plugin at all.
/// </para>
/// <para>
/// The assembly written here is not a real one. Loading it is expected to fail,
/// which is fine: what these assert is where the files land and what gets
/// refused, and both are decided before anything is handed to the loader.
/// </para>
/// </summary>
public class PluginArchiveInstallTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-archive-{Ulid.NewUlid():N}"
    );
    private readonly string _pluginsDir;
    private readonly PluginManager _manager;

    public PluginArchiveInstallTests()
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

    private const string Manifest = """
        {
          "id": "5KTKRT4Z2Y9P59Y40W5CX4TQKF",
          "name": "Internet Radio Provider",
          "description": "Adds internet radio stations as a music media source.",
          "version": "1.0.0",
          "targetAbi": "12.0",
          "author": "NoMercy Community",
          "assembly": "NoMercy.Plugin.InternetRadio.dll"
        }
        """;

    private static void Write(ZipArchive archive, string entryName, string content)
    {
        using StreamWriter writer = new(archive.CreateEntry(entryName).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    /// <summary>The published layout: one folder holding README, manifest and assembly.</summary>
    private string PublishedArchive(string name = "radio.zip")
    {
        string path = Path.Combine(_tempDir, name);

        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "NoMercy.Plugin.InternetRadio/README.md", "# Internet Radio");
        Write(archive, "NoMercy.Plugin.InternetRadio/plugin.json", Manifest);
        Write(archive, "NoMercy.Plugin.InternetRadio/NoMercy.Plugin.InternetRadio.dll", "MZ");

        return path;
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static string Sha256Of(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private async Task InstallIgnoringLoad(string archivePath, string? checksum = null)
    {
        try
        {
            await _manager.InstallPluginArchiveAsync(archivePath, checksum, CancellationToken.None);
        }
        catch (BadImageFormatException)
        {
            // Everything under test happened before the loader saw the file.
        }
    }

    [Fact]
    public async Task PublishedArchive_UnpacksTheWholeFolderNotJustTheAssembly()
    {
        await InstallIgnoringLoad(PublishedArchive());

        string installed = Path.Combine(_pluginsDir, "NoMercy.Plugin.InternetRadio");

        File.Exists(Path.Combine(installed, "plugin.json"))
            .Should()
            .BeTrue("the manifest is what the loader reads to know what this plugin is");
        File.Exists(Path.Combine(installed, "NoMercy.Plugin.InternetRadio.dll")).Should().BeTrue();
        File.Exists(Path.Combine(installed, "README.md"))
            .Should()
            .BeTrue("everything the plugin shipped with comes across, not a chosen subset");
    }

    [Fact]
    public async Task PublishedArchive_StripsTheWrappingFolderRatherThanNestingIt()
    {
        await InstallIgnoringLoad(PublishedArchive());

        Directory
            .Exists(
                Path.Combine(
                    _pluginsDir,
                    "NoMercy.Plugin.InternetRadio",
                    "NoMercy.Plugin.InternetRadio"
                )
            )
            .Should()
            .BeFalse("the archive's own folder is the plugin folder, not a child of it");
    }

    [Fact]
    public async Task MatchingChecksum_Installs()
    {
        string path = PublishedArchive();

        await InstallIgnoringLoad(path, Sha256Of(path));

        File.Exists(Path.Combine(_pluginsDir, "NoMercy.Plugin.InternetRadio", "plugin.json"))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task ChecksumMismatch_RefusesAndUnpacksNothing()
    {
        string path = PublishedArchive();

        Func<Task> act = () =>
            _manager.InstallPluginArchiveAsync(path, new string('a', 64), CancellationToken.None);

        await act.Should().ThrowAsync<PluginVerificationException>();
        Directory
            .Exists(Path.Combine(_pluginsDir, "NoMercy.Plugin.InternetRadio"))
            .Should()
            .BeFalse("a rejected archive must not leave a folder the loader would find on boot");
    }

    /// <summary>
    /// A zip names its own entries, so an entry may name a path. This is why the
    /// extractor resolves every destination itself instead of calling
    /// ExtractToDirectory.
    /// </summary>
    [Fact]
    public async Task EntryEscapingTheFolder_IsRefusedAndNothingIsWrittenOutside()
    {
        string path = Path.Combine(_tempDir, "escape.zip");

        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(archive, "Example/plugin.json", Manifest);
            Write(archive, "Example/NoMercy.Plugin.InternetRadio.dll", "MZ");
            Write(archive, "Example/../../escaped.dll", "MZ");
        }

        Func<Task> act = () =>
            _manager.InstallPluginArchiveAsync(path, null, CancellationToken.None);

        await act.Should().ThrowAsync<PluginVerificationException>();
        File.Exists(Path.Combine(_tempDir, "escaped.dll")).Should().BeFalse();
    }

    [Fact]
    public async Task ArchiveWithoutAManifest_IsRefused()
    {
        string path = Path.Combine(_tempDir, "no-manifest.zip");

        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(archive, "Something/Something.dll", "MZ");
        }

        Func<Task> act = () =>
            _manager.InstallPluginArchiveAsync(path, null, CancellationToken.None);

        await act.Should().ThrowAsync<PluginVerificationException>();
    }

    [Fact]
    public async Task ManifestNamingAnAssemblyTheArchiveLacks_IsRefused()
    {
        string path = Path.Combine(_tempDir, "missing-assembly.zip");

        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(archive, "Example/plugin.json", Manifest);
            Write(archive, "Example/SomethingElse.dll", "MZ");
        }

        Func<Task> act = () =>
            _manager.InstallPluginArchiveAsync(path, null, CancellationToken.None);

        await act.Should().ThrowAsync<PluginVerificationException>();
    }

    [Fact]
    public async Task MissingArchive_IsAFileNotFoundRatherThanASilentNoOp()
    {
        Func<Task> act = () =>
            _manager.InstallPluginArchiveAsync(
                Path.Combine(_tempDir, "nope.zip"),
                null,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
