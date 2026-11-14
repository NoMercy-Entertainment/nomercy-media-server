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
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Verification;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace NoMercy.Tests.Plugins;

// The marketplace signs a canonical subject, not the package bytes. The server
// rebuilds it from the index entry; one different byte and nothing verifies.
public class PluginIndexSignatureVerificationTests : IDisposable
{
    private const string PluginId = "01J0000000000000000000000A";
    private const string PublisherId = "00000000-0000-4000-8000-0000000000aa";
    private const string KeyId = "mk_test";

    /// <summary>The vector both repos assert literally (the marketplace's signer makes it).</summary>
    private const string SharedSubject =
        "{\"capabilities\":[\"network.fetch\"],\"id\":\"01J0000000000000000000000A\",\"publisher\":\"00000000-0000-4000-8000-0000000000aa\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"tier\":\"free\",\"version\":\"1.0.0\"}";

    private static readonly JsonSerializerOptions IndexOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-index-signature-{Ulid.NewUlid():N}"
    );

    public PluginIndexSignatureVerificationTests()
    {
        Directory.CreateDirectory(_tempDir);
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

    private static (string PublicKey, Ed25519PrivateKeyParameters Private) Keys()
    {
        Ed25519KeyPairGenerator generator = new();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

        Ed25519PrivateKeyParameters privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        Ed25519PublicKeyParameters publicKey = (Ed25519PublicKeyParameters)pair.Public;

        return (Convert.ToBase64String(publicKey.GetEncoded()), privateKey);
    }

    private static string Sign(byte[] content, Ed25519PrivateKeyParameters privateKey)
    {
        Ed25519Signer signer = new();
        signer.Init(true, privateKey);
        signer.BlockUpdate(content, 0, content.Length);

        return Convert.ToBase64String(signer.GenerateSignature());
    }

    private static string Sha256Of(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>The subject as the marketplace's signer writes it, built without the server's builder.</summary>
    private static string MarketplaceSubject(string sha256) =>
        $"{{\"capabilities\":[\"network.fetch\"],\"id\":\"{PluginId}\",\"publisher\":\"{PublisherId}\",\"sha256\":\"{sha256}\",\"tier\":\"free\",\"version\":\"1.0.0\"}}";

    /// <summary>One plugin in the shape the marketplace index publishes, plus <c>publisherId</c>.</summary>
    private static string IndexJson(
        string checksum,
        string signature,
        string capabilities = "[\"network.fetch\"]",
        string publisherId = $",\"publisherId\":\"{PublisherId}\""
    ) =>
        "{\"name\":\"Example Marketplace\",\"url\":\"https://example.com/index.json\",\"plugins\":[{"
        + $"\"id\":\"{PluginId}\",\"name\":\"Snapshot Radio\",\"description\":\"Stations.\","
        + "\"author\":\"Made Up Studio\",\"projectUrl\":\"https://example.com/radio\",\"tier\":\"free\","
        + "\"versions\":[{\"version\":\"1.0.0\",\"downloadUrl\":\"https://example.com/radio-1.0.0.zip\","
        + $"\"targetAbi\":\"12.0\",\"checksum\":\"{checksum}\",\"changelog\":\"First.\","
        + "\"timestamp\":\"2026-10-01T12:00:00+00:00\","
        + $"\"signature\":\"{signature}\",\"kid\":\"{KeyId}\",\"capabilities\":{capabilities}}}],"
        + $"\"publisher\":\"Made Up Studio\"{publisherId},\"trust\":\"medium\","
        + "\"maintenance\":\"maintained\",\"rating\":4.5}]}";

    private static PluginIndexSignature? FromIndex(string json)
    {
        PluginRepositoryManifest manifest = JsonSerializer.Deserialize<PluginRepositoryManifest>(
            json,
            IndexOptions
        )!;
        PluginRepositoryEntry entry = manifest.Plugins.Single();

        return PluginIndexSignature.From(entry, entry.Versions.Single());
    }

    private string WritePackage(byte[] content)
    {
        string path = Path.Combine(_tempDir, $"radio-{Ulid.NewUlid():N}.zip");
        File.WriteAllBytes(path, content);

        return path;
    }

    private static PluginVerificationContext Context(
        string packagePath,
        PluginIndexSignature? index,
        PluginSignatureBlock? manifestSignature = null
    ) =>
        new()
        {
            Manifest = new()
            {
                Id = new(Ulid.Parse(PluginId)),
                Name = "Snapshot Radio",
                Description = "Stations.",
                Version = "1.0.0",
                TargetAbi = "12.0",
                Assembly = "Sample.dll",
            },
            AssemblyPath = "Sample.dll",
            PackagePath = packagePath,
            Signature = manifestSignature,
            IndexSignature = index,
            FromMarketplace = true,
        };

    private static SignatureVerificationStage Stage(string publicKey) =>
        new(new PluginTrustedKeys(new Dictionary<string, string> { [KeyId] = publicKey }));

    [Fact]
    public void The_subject_built_from_an_index_entry_is_the_shared_vector_byte_for_byte()
    {
        PluginIndexSignature index = FromIndex(IndexJson(new('a', 64), "sig"))!;

        PluginSignatureSubject.Build(index).Should().Be(SharedSubject);
    }

    [Fact]
    public void A_signature_over_the_shared_vector_verifies_against_the_built_subject()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        string signature = Sign(Encoding.UTF8.GetBytes(SharedSubject), privateKey);
        PluginIndexSignature index = FromIndex(IndexJson(new('a', 64), signature))!;

        PluginEd25519
            .Verify(
                Encoding.UTF8.GetBytes(PluginSignatureSubject.Build(index)),
                index.Signature,
                publicKey
            )
            .Should()
            .BeTrue();
    }

    [Fact]
    public void An_empty_capability_list_is_written_as_an_empty_array()
    {
        PluginIndexSignature index = FromIndex(IndexJson(new('a', 64), "sig", "[]"))!;

        PluginSignatureSubject.Build(index).Should().StartWith("{\"capabilities\":[],\"id\":");
    }

    [Fact]
    public void Text_is_escaped_the_way_the_marketplace_escapes_it()
    {
        PluginIndexSignature index = new(
            Signature: "sig",
            KeyId: KeyId,
            PluginId: PluginId,
            Version: "1.0.0",
            Sha256: new('a', 64),
            PublisherId: PublisherId,
            Tier: "free",
            Capabilities: ["a/b", "café", "say \"hi\"\\\n", "\U0001F600", "<&'>\u007f"]
        );

        PluginSignatureSubject
            .Build(index)
            .Should()
            .StartWith(
                "{\"capabilities\":[\"a/b\",\"caf\\u00e9\",\"say \\\"hi\\\"\\\\\\n\",\"\\ud83d\\ude00\",\"<&'>\u007f\"],\"id\":"
            );
    }

    [Fact]
    public void A_package_signed_the_marketplace_way_verifies_from_the_index()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        byte[] package = [1, 2, 3, 4, 5];
        string sha256 = Sha256Of(package);
        string signature = Sign(Encoding.UTF8.GetBytes(MarketplaceSubject(sha256)), privateKey);

        (PluginStageOutcome outcome, string? message) = Stage(publicKey)
            .Evaluate(Context(WritePackage(package), FromIndex(IndexJson(sha256, signature))));

        message.Should().BeNull();
        outcome.Should().Be(PluginStageOutcome.Pass);
    }

    [Fact]
    public void A_signed_sha256_in_upper_case_still_matches_the_package()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        byte[] package = [1, 2, 3, 4, 5];
        string sha256 = Sha256Of(package).ToUpperInvariant();
        string signature = Sign(Encoding.UTF8.GetBytes(MarketplaceSubject(sha256)), privateKey);

        (PluginStageOutcome outcome, _) = Stage(publicKey)
            .Evaluate(Context(WritePackage(package), FromIndex(IndexJson(sha256, signature))));

        outcome.Should().Be(PluginStageOutcome.Pass);
    }

    [Fact]
    public void A_package_with_one_byte_changed_fails_on_the_signed_sha256()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        string sha256 = Sha256Of([1, 2, 3, 4, 5]);
        string signature = Sign(Encoding.UTF8.GetBytes(MarketplaceSubject(sha256)), privateKey);

        (PluginStageOutcome outcome, string? message) = Stage(publicKey)
            .Evaluate(
                Context(WritePackage([1, 2, 3, 4, 6]), FromIndex(IndexJson(sha256, signature)))
            );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("sha256").And.Contain(sha256);
    }

    [Fact]
    public void A_capability_added_to_the_entry_after_signing_fails()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        byte[] package = [1, 2, 3, 4, 5];
        string sha256 = Sha256Of(package);
        string signature = Sign(Encoding.UTF8.GetBytes(MarketplaceSubject(sha256)), privateKey);

        (PluginStageOutcome outcome, string? message) = Stage(publicKey)
            .Evaluate(
                Context(
                    WritePackage(package),
                    FromIndex(IndexJson(sha256, signature, "[\"network.fetch\",\"files.write\"]"))
                )
            );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("capabilities");
    }

    [Fact]
    public void An_index_signature_without_a_publisher_id_fails_and_names_the_field()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        byte[] package = [1, 2, 3, 4, 5];
        string sha256 = Sha256Of(package);
        string signature = Sign(Encoding.UTF8.GetBytes(MarketplaceSubject(sha256)), privateKey);

        (PluginStageOutcome outcome, string? message) = Stage(publicKey)
            .Evaluate(
                Context(
                    WritePackage(package),
                    FromIndex(IndexJson(sha256, signature, publisherId: string.Empty))
                )
            );

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("publisherId");
    }

    [Fact]
    public void An_index_signature_without_a_tier_fails_and_names_the_field()
    {
        (string publicKey, _) = Keys();
        PluginIndexSignature index = FromIndex(IndexJson(new('a', 64), "sig"))! with
        {
            Tier = null,
        };

        (PluginStageOutcome outcome, string? message) = Stage(publicKey)
            .Evaluate(Context(WritePackage([1, 2, 3]), index));

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("tier");
    }

    [Fact]
    public void An_old_index_without_the_new_fields_parses_as_before()
    {
        const string oldIndex = """
            {
              "name": "Example Repository",
              "plugins": [
                {
                  "id": "01J0000000000000000000000A",
                  "name": "Snapshot Radio",
                  "description": "Stations.",
                  "versions": [
                    {
                      "version": "1.0.0",
                      "downloadUrl": "https://example.com/radio-1.0.0.zip",
                      "checksum": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    }
                  ]
                }
              ]
            }
            """;

        PluginRepositoryManifest manifest = JsonSerializer.Deserialize<PluginRepositoryManifest>(
            oldIndex,
            IndexOptions
        )!;
        PluginRepositoryEntry entry = manifest.Plugins.Single();
        PluginVersionEntry version = entry.Versions.Single();

        entry.PublisherId.Should().BeNull();
        entry.Tier.Should().Be(PluginTier.Free);
        version.Signature.Should().BeNull();
        version.KeyId.Should().BeNull();
        version.Capabilities.Should().BeEmpty();
        PluginIndexSignature.From(entry, version).Should().BeNull();
    }

    [Fact]
    public void A_manifest_signed_package_without_an_index_signature_verifies_as_before()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        byte[] package = [1, 2, 3, 4, 5];

        (PluginStageOutcome outcome, _) = Stage(publicKey)
            .Evaluate(
                Context(
                    WritePackage(package),
                    index: null,
                    manifestSignature: new("ed25519", KeyId, Sign(package, privateKey))
                )
            );

        outcome.Should().Be(PluginStageOutcome.Pass);
    }

    [Fact]
    public void A_package_with_neither_signature_fails_as_before()
    {
        (string publicKey, _) = Keys();

        (PluginStageOutcome outcome, string? message) = Stage(publicKey)
            .Evaluate(Context(WritePackage([1, 2, 3]), index: null));

        outcome.Should().Be(PluginStageOutcome.Fail);
        message.Should().Contain("without a signature");
    }

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

    private PluginManager Manager(string publicKey, string pluginsDir) =>
        new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            pluginsDir,
            TestStorageHelper.CreateStorage(pluginsDir),
            TestStorageHelper.CreateBackend(),
            new PluginVerifier([Stage(publicKey)])
        );

    [Fact]
    public async Task A_repository_archive_signed_in_the_index_installs()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        string pluginsDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        string archive = Archive();
        string sha256 = Sha256Of(File.ReadAllBytes(archive));
        string signature = Sign(Encoding.UTF8.GetBytes(MarketplaceSubject(sha256)), privateKey);
        using PluginManager manager = Manager(publicKey, pluginsDir);

        try
        {
            await manager.InstallPluginArchiveAsync(
                archive,
                sha256,
                CancellationToken.None,
                fromMarketplace: true,
                indexSignature: FromIndex(IndexJson(sha256, signature))
            );
        }
        catch (BadImageFormatException)
        {
            // The fake assembly fails to load; everything under test happened first.
        }

        File.Exists(Path.Combine(pluginsDir, "Example.Plugin.Radio", "plugin.json"))
            .Should()
            .BeTrue("the index signature is the only one this package has");
    }

    [Fact]
    public async Task A_repository_archive_whose_index_entry_changed_after_signing_is_refused()
    {
        (string publicKey, Ed25519PrivateKeyParameters privateKey) = Keys();
        string pluginsDir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        string archive = Archive();
        string sha256 = Sha256Of(File.ReadAllBytes(archive));
        string signature = Sign(Encoding.UTF8.GetBytes(MarketplaceSubject(sha256)), privateKey);
        using PluginManager manager = Manager(publicKey, pluginsDir);

        Func<Task> install = () =>
            manager.InstallPluginArchiveAsync(
                archive,
                sha256,
                CancellationToken.None,
                fromMarketplace: true,
                indexSignature: FromIndex(
                    IndexJson(sha256, signature, "[\"network.fetch\",\"files.write\"]")
                )
            );

        await install.Should().ThrowAsync<PluginVerificationException>();
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
