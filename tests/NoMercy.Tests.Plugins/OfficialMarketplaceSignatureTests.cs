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
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Provenance;
using NoMercy.PluginSdk.Verification;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace NoMercy.Tests.Plugins;

// The official marketplace signs every version it lists, so a signature that
// fails there is refused in every trust mode. Other repositories only warn.
public class OfficialMarketplaceSignatureTests : IDisposable
{
    private const string OfficialIndex = "https://api.nomercy.tv/v1/marketplace/index.json";
    private const string OtherIndex = "https://example.com/index.json";
    private const string PluginId = "01J0000000000000000000000A";
    private const string PublisherId = "00000000-0000-4000-8000-0000000000aa";
    private const string KeyId = "mk_test";
    private const string NoPlugins = "{\"name\":\"Empty\",\"plugins\":[]}";

    private const string ArchiveManifest = """
        {
          "id": "01J0000000000000000000000A",
          "name": "Snapshot Radio",
          "description": "Stations.",
          "version": "1.0.0",
          "targetAbi": "12.0",
          "author": "Made Up Studio",
          "assembly": "Example.Plugin.Radio.dll"
        }
        """;

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"nm-official-marketplace-{Ulid.NewUlid():N}"
    );
    private readonly string _pluginsDir;
    private readonly string _archive;
    private readonly string _sha256;
    private readonly string _publicKey;
    private readonly string _validSignature;
    private readonly ListLogger _log = new();
    private readonly IndexHandler _indexes = new();

    public OfficialMarketplaceSignatureTests()
    {
        _pluginsDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(_pluginsDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "catalog"));

        _archive = Archive();
        _sha256 = Convert
            .ToHexString(SHA256.HashData(File.ReadAllBytes(_archive)))
            .ToLowerInvariant();

        Ed25519KeyPairGenerator generator = new();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();
        _publicKey = Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());

        byte[] subject = Encoding.UTF8.GetBytes(
            $"{{\"capabilities\":[\"network.fetch\"],\"id\":\"{PluginId}\",\"publisher\":\"{PublisherId}\",\"sha256\":\"{_sha256}\",\"tier\":\"free\",\"version\":\"1.0.0\"}}"
        );
        Ed25519Signer signer = new();
        signer.Init(true, pair.Private);
        signer.BlockUpdate(subject, 0, subject.Length);
        _validSignature = Convert.ToBase64String(signer.GenerateSignature());

        _indexes.Bodies[OfficialIndex] = NoPlugins;
        _indexes.Bodies[OtherIndex] = NoPlugins;
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

    private string Archive()
    {
        string path = Path.Combine(_tempDir, "radio.zip");

        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (
            StreamWriter writer = new(
                archive.CreateEntry("Example.Plugin.Radio/plugin.json").Open(),
                Encoding.UTF8
            )
        )
            writer.Write(ArchiveManifest);
        using (
            StreamWriter writer = new(
                archive.CreateEntry("Example.Plugin.Radio/Example.Plugin.Radio.dll").Open(),
                Encoding.UTF8
            )
        )
            writer.Write("MZ");

        return path;
    }

    /// <summary>One plugin as an index lists it; <paramref name="signed"/> is the signature part.</summary>
    private string Index(string signed) =>
        "{\"name\":\"Example Index\",\"plugins\":[{"
        + $"\"id\":\"{PluginId}\",\"name\":\"Snapshot Radio\",\"description\":\"Stations.\","
        + $"\"tier\":\"free\",\"publisherId\":\"{PublisherId}\","
        + "\"versions\":[{\"version\":\"1.0.0\",\"downloadUrl\":\"https://example.com/radio-1.0.0.zip\","
        + $"\"targetAbi\":\"12.0\",\"checksum\":\"{_sha256}\"{signed}}}]}}]}}";

    private string SignedIndex(string capabilities = "[\"network.fetch\"]") =>
        Index(
            $",\"signature\":\"{_validSignature}\",\"kid\":\"{KeyId}\",\"capabilities\":{capabilities}"
        );

    private string UnsignedIndex() => Index(string.Empty);

    /// <summary>A capability added after the marketplace signed the version.</summary>
    private string TamperedIndex() => SignedIndex("[\"network.fetch\",\"files.write\"]");

    private async Task<PluginRepository> CatalogAsync(params string[] repositoryUrls)
    {
        string catalogDir = Path.Combine(_tempDir, "catalog");

        if (repositoryUrls.Length > 0)
        {
            Directory.CreateDirectory(Path.Combine(catalogDir, "configurations"));
            File.WriteAllText(
                Path.Combine(catalogDir, "configurations", "repositories.json"),
                JsonSerializer.Serialize(
                    repositoryUrls.Select(url => new PluginRepositoryInfo { Name = url, Url = url })
                )
            );
        }

        PluginRepository repository = new(
            new(_indexes),
            NullLogger.Instance,
            catalogDir,
            TestStorageHelper.CreateStorage(catalogDir)
        );
        await repository.LoadRepositoriesFromDiskAsync();
        await repository.RefreshAsync();

        return repository;
    }

    /// <summary>The default configuration: the shipped key, and enforcing off.</summary>
    private PluginManager WarnOnlyManager() =>
        new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            TestStorageHelper.CreateBackend(),
            new PluginVerifier([
                new SignatureVerificationStage(
                    new PluginTrustedKeys(new Dictionary<string, string> { [KeyId] = _publicKey }),
                    new(enforce: false, _log)
                ),
            ])
        );

    /// <summary>The repository controller's install, without the download.</summary>
    private async Task InstallAsync(PluginRepository catalog, PluginManager manager)
    {
        PluginRepositoryEntry entry = catalog.FindPlugin(Ulid.Parse(PluginId))!;
        PluginVersionEntry version = entry.Versions.Single();

        try
        {
            await manager.InstallFromRepositoryAsync(
                _archive,
                version.Checksum,
                PluginIndexSignature.From(entry, version),
                catalog.IsFromOfficialMarketplace(entry),
                catalog.ListedBy(entry)
            );
        }
        catch (BadImageFormatException)
        {
            // The fake assembly fails to load; everything under test happened first.
        }
    }

    private bool Installed =>
        File.Exists(Path.Combine(_pluginsDir, "Example.Plugin.Radio", "plugin.json"));

    private bool WarnedOnly =>
        _log.Entries.Any(entry =>
            entry.Message.StartsWith(PluginTrustMode.LogPrefix, StringComparison.Ordinal)
        );

    [Fact]
    public async Task An_official_marketplace_entry_changed_after_signing_is_refused_with_enforcing_off()
    {
        _indexes.Bodies[OfficialIndex] = TamperedIndex();
        PluginRepository catalog = await CatalogAsync();
        using PluginManager manager = WarnOnlyManager();

        Func<Task> install = () => InstallAsync(catalog, manager);

        await install.Should().ThrowAsync<PluginVerificationException>();
        Installed.Should().BeFalse();
        WarnedOnly.Should().BeFalse("the official marketplace is refused, not warned about");
    }

    [Fact]
    public async Task An_official_marketplace_entry_with_a_valid_signature_installs_with_enforcing_off()
    {
        _indexes.Bodies[OfficialIndex] = SignedIndex();
        PluginRepository catalog = await CatalogAsync();
        using PluginManager manager = WarnOnlyManager();

        await InstallAsync(catalog, manager);

        Installed.Should().BeTrue();
        WarnedOnly.Should().BeFalse();
    }

    [Fact]
    public async Task An_unsigned_entry_from_another_repository_installs_and_the_warning_is_logged()
    {
        _indexes.Bodies[OtherIndex] = UnsignedIndex();
        PluginRepository catalog = await CatalogAsync(OfficialIndex, OtherIndex);
        using PluginManager manager = WarnOnlyManager();

        await InstallAsync(catalog, manager);

        Installed.Should().BeTrue();
        WarnedOnly.Should().BeTrue("another repository keeps the warn-only behavior");
    }

    [Fact]
    public async Task A_plugin_both_repositories_list_is_refused_when_the_official_entry_is_the_one_used()
    {
        _indexes.Bodies[OfficialIndex] = TamperedIndex();
        _indexes.Bodies[OtherIndex] = UnsignedIndex();
        PluginRepository catalog = await CatalogAsync(OfficialIndex, OtherIndex);
        using PluginManager manager = WarnOnlyManager();

        Func<Task> install = () => InstallAsync(catalog, manager);

        await install.Should().ThrowAsync<PluginVerificationException>();
        Installed.Should().BeFalse();
    }

    [Fact]
    public async Task A_plugin_both_repositories_list_only_warns_when_the_other_entry_is_the_one_used()
    {
        _indexes.Bodies[OfficialIndex] = TamperedIndex();
        _indexes.Bodies[OtherIndex] = UnsignedIndex();
        PluginRepository catalog = await CatalogAsync(OtherIndex, OfficialIndex);
        using PluginManager manager = WarnOnlyManager();

        await InstallAsync(catalog, manager);

        Installed.Should().BeTrue();
        WarnedOnly.Should().BeTrue();
    }

    /// <summary>What the install wrote down, read from the platform store it shares with consent.</summary>
    private PluginInstallSource? RecordedSource() =>
        new ConfigPluginInstallSourceStore(
            new PluginConfiguration(
                Path.Combine(_pluginsDir, "data", "platform"),
                TestStorageHelper.CreateStorage(_pluginsDir)
            )
        ).Find(Ulid.Parse(PluginId));

    [Fact]
    public async Task An_official_marketplace_install_records_the_marketplace_as_its_source()
    {
        _indexes.Bodies[OfficialIndex] = SignedIndex();
        PluginRepository catalog = await CatalogAsync();
        using PluginManager manager = WarnOnlyManager();

        await InstallAsync(catalog, manager);

        PluginInstallSource source = RecordedSource()!;
        source.Kind.Should().Be(PluginInstallSourceKind.Repository);
        source.RepositoryName.Should().Be("NoMercy Marketplace");
        source.RepositoryUrl.Should().Be(OfficialIndex);
        source.FromOfficialMarketplace.Should().BeTrue();
        source.ChecksumVerified.Should().BeTrue();
    }

    [Fact]
    public async Task A_plugin_both_repositories_list_records_the_index_whose_entry_was_used()
    {
        _indexes.Bodies[OfficialIndex] = TamperedIndex();
        _indexes.Bodies[OtherIndex] = UnsignedIndex();
        PluginRepository catalog = await CatalogAsync(OtherIndex, OfficialIndex);
        using PluginManager manager = WarnOnlyManager();

        await InstallAsync(catalog, manager);

        PluginInstallSource source = RecordedSource()!;
        source.RepositoryUrl.Should().Be(OtherIndex);
        source.FromOfficialMarketplace.Should().BeFalse();
    }

    [Fact]
    public async Task A_refused_official_entry_records_no_source()
    {
        _indexes.Bodies[OfficialIndex] = TamperedIndex();
        PluginRepository catalog = await CatalogAsync();
        using PluginManager manager = WarnOnlyManager();

        Func<Task> install = () => InstallAsync(catalog, manager);

        await install.Should().ThrowAsync<PluginVerificationException>();
        RecordedSource().Should().BeNull();
    }

    private sealed class IndexHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Bodies[request.RequestUri!.ToString()]),
                }
            );
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
