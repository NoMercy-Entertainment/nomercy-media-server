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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Provenance;
using NoMercy.PluginSdk.Verification;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Every way the server installs or updates a plugin writes down where it
/// came from, at the moment it happened, in the platform store.
/// </summary>
public class PluginInstallSourceRecordingTests : IDisposable
{
    private const string Echo = "NoMercy.Plugin.Samples.Echo";
    private const string IndexUrl = "https://example.com/index.json";
    private static readonly Ulid EchoId = Ulid.Parse("01JECH0000000000000000000A");

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"nm-install-source-{Ulid.NewUlid():N}"
    );
    private readonly string _pluginsDir;
    private readonly PluginPlatformConfiguration _platform;
    private readonly ConfigPluginInstallSourceStore _sources;
    private readonly List<PluginManager> _managers = [];
    private readonly PluginManager _manager;

    public PluginInstallSourceRecordingTests()
    {
        _pluginsDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(_pluginsDir);

        _platform = new(_pluginsDir, TestStorageHelper.CreateStorage(_pluginsDir));
        _sources = new(_platform);
        _manager = Start();
    }

    /// <summary>One server start over the same plugins folder and platform store.</summary>
    private PluginManager Start()
    {
        PluginManager manager = new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            TestStorageHelper.CreateBackend(),
            // No signature stage: what is under test is the record, and the
            // published checksum is enforced before any stage runs.
            verifier: new PluginVerifier([]),
            installSourceStore: _sources,
            platformConfiguration: _platform
        );
        _managers.Add(manager);

        return manager;
    }

    public void Dispose()
    {
        foreach (PluginManager manager in _managers)
            manager.Dispose();

        // Echo is loaded for real; its shadow copy is only free once collected.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private static readonly PluginRepositoryInfo Index = new()
    {
        Name = "Example Index",
        Url = IndexUrl,
    };

    private string EchoArchive(string version = "1.2.0")
    {
        string path = Path.Combine(_tempDir, $"echo-{Ulid.NewUlid():N}.zip");

        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(
            CodeScanVerificationStageTests.SampleDllPath(Echo),
            $"Echo/{Echo}.dll"
        );
        using StreamWriter manifest = new(archive.CreateEntry("Echo/plugin.json").Open());
        manifest.Write(
            $$"""
            {
              "id": "{{EchoId}}",
              "name": "Echo",
              "version": "{{version}}",
              "description": "echoes",
              "assembly": "{{Echo}}.dll"
            }
            """
        );

        return path;
    }

    private static string Sha256Of(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private Task FromRepositoryAsync(
        string archive,
        string? checksum,
        bool official = false,
        PluginRepositoryInfo? listedBy = null
    ) => _manager.InstallFromRepositoryAsync(archive, checksum, null, official, listedBy ?? Index);

    [Fact]
    public async Task A_repository_install_records_the_index_the_official_flag_and_the_checksum()
    {
        string archive = EchoArchive();

        await FromRepositoryAsync(archive, Sha256Of(archive), official: true);

        PluginInstallSource source = _sources.Find(EchoId)!;
        source.Kind.Should().Be(PluginInstallSourceKind.Repository);
        source.RepositoryName.Should().Be("Example Index");
        source.RepositoryUrl.Should().Be(IndexUrl);
        source.FromOfficialMarketplace.Should().BeTrue();
        source.ChecksumVerified.Should().BeTrue();
        source.Version.Should().Be("1.2.0");
        source.RecordedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_repository_that_published_no_checksum_is_recorded_unverified()
    {
        await FromRepositoryAsync(EchoArchive(), checksum: null);

        PluginInstallSource source = _sources.Find(EchoId)!;
        source.Kind.Should().Be(PluginInstallSourceKind.Repository);
        source.FromOfficialMarketplace.Should().BeFalse();
        source.ChecksumVerified.Should().BeFalse();
    }

    [Fact]
    public async Task An_uploaded_archive_is_recorded_as_a_file()
    {
        await _manager.InstallPluginArchiveAsync(EchoArchive());

        PluginInstallSource source = _sources.Find(EchoId)!;
        source.Kind.Should().Be(PluginInstallSourceKind.File);
        source.RepositoryName.Should().BeNull();
        source.RepositoryUrl.Should().BeNull();
        source.FromOfficialMarketplace.Should().BeFalse();
        source.ChecksumVerified.Should().BeFalse();
    }

    [Fact]
    public async Task An_uploaded_assembly_is_recorded_as_a_file_under_the_id_it_loaded_with()
    {
        await _manager.InstallPluginAsync(CodeScanVerificationStageTests.SampleDllPath(Echo));

        PluginInfo installed = _manager.GetInstalledPlugins().Should().ContainSingle().Subject;
        PluginInstallSource source = _sources.Find(installed.Id)!;
        source.Kind.Should().Be(PluginInstallSourceKind.File);
        source.ChecksumVerified.Should().BeFalse();
        source.Version.Should().Be(installed.Version.ToString());
    }

    [Fact]
    public async Task An_update_replaces_what_the_first_install_recorded()
    {
        await _manager.InstallPluginArchiveAsync(EchoArchive("1.2.0"));
        _sources.Find(EchoId)!.Kind.Should().Be(PluginInstallSourceKind.File);

        string update = EchoArchive("1.3.0");
        await FromRepositoryAsync(update, Sha256Of(update));

        PluginInstallSource source = _sources.Find(EchoId)!;
        source.Kind.Should().Be(PluginInstallSourceKind.Repository);
        source.RepositoryUrl.Should().Be(IndexUrl);
        source.ChecksumVerified.Should().BeTrue();
        source.Version.Should().Be("1.3.0");
    }

    [Fact]
    public async Task A_package_refused_for_its_checksum_records_nothing()
    {
        Func<Task> install = () => FromRepositoryAsync(EchoArchive(), new string('0', 64));

        await install.Should().ThrowAsync<PluginVerificationException>();
        _sources.Find(EchoId).Should().BeNull();
    }

    [Fact]
    public async Task Uninstalling_forgets_the_source_even_when_the_data_is_kept()
    {
        await _manager.InstallPluginArchiveAsync(EchoArchive());

        await _manager.UninstallPluginAsync(EchoId, keepData: true);

        _sources.Find(EchoId).Should().BeNull();
    }

    [Fact]
    public async Task The_record_shares_the_platform_file_without_touching_the_grants()
    {
        ConfigPluginGrantStore grants = new(_platform);
        grants.Grant(EchoId, PluginGrantKind.NetworkHost, PluginGrant.Everything);

        await _manager.InstallPluginArchiveAsync(EchoArchive());

        _sources.Find(EchoId).Should().NotBeNull();
        grants.Holds(EchoId, PluginGrantKind.NetworkHost, "example.com").Should().BeTrue();
    }

    [Fact]
    public async Task A_bare_assembly_that_fails_to_load_leaves_no_earlier_record_behind()
    {
        Ulid earlier = Ulid.Parse("01JEAR11ER0000000000000000");
        _sources.Record(
            earlier,
            new()
            {
                Kind = PluginInstallSourceKind.Repository,
                Folder = "Broken.Plugin",
                RepositoryUrl = "https://example.com/index.json",
            }
        );
        string upload = Path.Combine(_tempDir, "Broken.Plugin.dll");
        await File.WriteAllTextAsync(upload, "not an assembly");

        try
        {
            await _manager.InstallPluginAsync(upload);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            // Refusing the load is fine; what it must not do is keep the old source.
        }

        _sources.Find(earlier).Should().BeNull();
    }

    [Fact]
    public async Task A_staged_update_s_source_is_committed_when_the_next_start_applies_it()
    {
        await _manager.InstallPluginArchiveAsync(EchoArchive());
        _manager.Dispose();

        // The update the hot swap could not apply, waiting for this start.
        string staging = Path.Combine(_pluginsDir, ".pending-updates", Echo);
        CopyFolder(Path.Combine(_pluginsDir, Echo), staging);
        PluginInstallSource update = new()
        {
            Kind = PluginInstallSourceKind.Repository,
            Folder = Echo,
            RepositoryName = "Example Index",
            RepositoryUrl = IndexUrl,
            ChecksumVerified = true,
            Version = "1.3.0",
        };
        _sources.Stage(Echo, EchoId, update);
        _sources.Find(EchoId)!.Kind.Should().Be(PluginInstallSourceKind.File, "not applied yet");

        await Start().LoadPluginsFromDirectoryAsync();

        _sources.Find(EchoId)!.RepositoryUrl.Should().Be(IndexUrl);
        _sources.StagedFolders().Should().BeEmpty();
    }

    [Fact]
    public async Task A_staged_source_whose_copy_is_gone_is_dropped_at_the_next_start()
    {
        _sources.Stage(
            "Gone",
            EchoId,
            new() { Kind = PluginInstallSourceKind.Repository, RepositoryUrl = IndexUrl }
        );

        await Start().LoadPluginsFromDirectoryAsync();

        _sources.StagedFolders().Should().BeEmpty();
        _sources.Find(EchoId).Should().BeNull("a copy nobody applied is nobody's source");
    }

    [Fact]
    public async Task A_new_install_over_a_staged_copy_drops_the_staged_source()
    {
        _sources.Stage(
            Echo,
            EchoId,
            new() { Kind = PluginInstallSourceKind.Repository, RepositoryUrl = IndexUrl }
        );

        await _manager.InstallPluginArchiveAsync(EchoArchive());

        _sources.StagedFolders().Should().BeEmpty();
        _sources.Find(EchoId)!.Kind.Should().Be(PluginInstallSourceKind.File);
    }

    [Fact]
    public async Task A_repository_install_that_names_no_repository_is_recorded_unknown()
    {
        await _manager.InstallPluginArchiveAsync(EchoArchive(), fromMarketplace: true);

        PluginInstallSource source = _sources.Find(EchoId)!;
        source.Kind.Should().Be(PluginInstallSourceKind.Unknown);
        source.RepositoryUrl.Should().BeNull();
    }

    [Fact]
    public async Task An_install_into_a_folder_replaces_a_record_another_id_left_there()
    {
        Ulid previous = Ulid.Parse("01JPREV10VS000000000000000");
        _sources.Record(
            previous,
            new() { Kind = PluginInstallSourceKind.Repository, Folder = Echo }
        );

        await _manager.InstallPluginArchiveAsync(EchoArchive());

        _sources.Find(previous).Should().BeNull();
        _sources.Find(EchoId).Should().NotBeNull();
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
