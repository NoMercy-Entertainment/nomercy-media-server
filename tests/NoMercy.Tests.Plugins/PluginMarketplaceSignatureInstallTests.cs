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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The stage answers correctly on its own; these ask whether anything calls it.
/// A signature check the install path never reaches is a check that refuses
/// nothing, and it would report clean forever.
/// </summary>
public class PluginMarketplaceSignatureInstallTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-signed-{Ulid.NewUlid():N}"
    );
    private readonly string _pluginsDir;
    private readonly PluginManager _manager;

    public PluginMarketplaceSignatureInstallTests()
    {
        _pluginsDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(_pluginsDir);

        _manager = new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            TestStorageHelper.CreateBackend(),
            new PluginVerifier([
                new SignatureVerificationStage(
                    new PluginTrustedKeys(
                        new Dictionary<string, string>
                        {
                            // A real key, so nothing here passes by being unreadable.
                            ["nomercy-1"] = "kAWHvNu0+g0K7c4pbPIsM1dSiHcKN0xB5GxLSXpqvxE=",
                        }
                    )
                ),
            ])
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
        catch (IOException) { }

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

    private string UnsignedArchive()
    {
        string path = Path.Combine(_tempDir, "radio.zip");

        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (
            StreamWriter writer = new(
                archive.CreateEntry("NoMercy.Plugin.InternetRadio/plugin.json").Open(),
                Encoding.UTF8
            )
        )
            writer.Write(Manifest);
        using (
            StreamWriter writer = new(
                archive
                    .CreateEntry("NoMercy.Plugin.InternetRadio/NoMercy.Plugin.InternetRadio.dll")
                    .Open(),
                Encoding.UTF8
            )
        )
            writer.Write("MZ");

        return path;
    }

    private string Installed => Path.Combine(_pluginsDir, "NoMercy.Plugin.InternetRadio");

    [Fact]
    public async Task A_repository_archive_with_no_signature_is_refused()
    {
        Func<Task> install = () =>
            _manager.InstallPluginArchiveAsync(
                UnsignedArchive(),
                expectedChecksum: null,
                CancellationToken.None,
                fromMarketplace: true
            );

        await install.Should().ThrowAsync<PluginVerificationException>();
    }

    [Fact]
    public async Task A_refused_archive_leaves_nothing_on_disk()
    {
        try
        {
            await _manager.InstallPluginArchiveAsync(
                UnsignedArchive(),
                expectedChecksum: null,
                CancellationToken.None,
                fromMarketplace: true
            );
        }
        catch (PluginVerificationException) { }

        Directory
            .EnumerateFileSystemEntries(_pluginsDir, "*", SearchOption.AllDirectories)
            .Should()
            .BeEmpty(
                "a package refused after unpacking is a package that was on disk to load, staging folder included"
            );
    }

    [Fact]
    public async Task The_same_archive_uploaded_by_the_owner_installs()
    {
        try
        {
            await _manager.InstallPluginArchiveAsync(
                UnsignedArchive(),
                expectedChecksum: null,
                CancellationToken.None
            );
        }
        catch (BadImageFormatException)
        {
            // The fake assembly fails to load; everything under test happened first.
        }

        File.Exists(Path.Combine(Installed, "plugin.json"))
            .Should()
            .BeTrue("refusing a sideload would take away the one path that works today");
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
