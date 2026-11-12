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

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Verification;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// AUD-1134: a signed list is matched by listing, version and package hash.
/// Each case runs the signed text, the store and the check before a plugin runs.
/// </summary>
public class PluginRevocationMatchTests : IDisposable
{
    private const string Listing = "01J9ZK5V8Y0000000000000001";
    private const string OtherListing = "01J9ZK5V8Y0000000000000002";
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string OtherHash =
        "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752";
    private const string IssuedAt = "2026-09-20T12:00:00+00:00";

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 13, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nm-revocation-match-{Ulid.NewUlid():N}"
    );

    private readonly string _publicKey;
    private readonly Ed25519PrivateKeyParameters _privateKey;

    public PluginRevocationMatchTests()
    {
        Ed25519KeyPairGenerator generator = new();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

        _privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        _publicKey = Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
    }

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

    private string Sign(string payload)
    {
        Ed25519Signer signer = new();
        signer.Init(true, _privateKey);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        signer.BlockUpdate(bytes, 0, bytes.Length);

        return Convert.ToBase64String(signer.GenerateSignature());
    }

    private string Body(string entries) =>
        JsonSerializer.Serialize(
            new
            {
                issued_at = IssuedAt,
                entries = JsonDocument.Parse(entries).RootElement,
                refresh_by = "2026-09-27T12:00:00+00:00",
                signature = new
                {
                    kid = "nomercy-1",
                    alg = "ed25519",
                    value = Sign($"{IssuedAt}{entries}"),
                },
            }
        );

    /// <summary>One entry in the shape the marketplace publishes.</summary>
    private static string Published(string listing, string? version, string? sha256) =>
        JsonSerializer.Serialize(
            new[]
            {
                new
                {
                    listing_id = listing,
                    version,
                    sha256,
                    severity = "critical",
                    reason = "plugins.revoked.security",
                    effective_at = "2026-09-20T11:00:00+00:00",
                },
            }
        );

    /// <summary>One entry in the shape this server read before.</summary>
    private static string Earlier(string pluginId, string hash) =>
        JsonSerializer.Serialize(
            new[]
            {
                new
                {
                    plugin_id = pluginId,
                    hash,
                    reason = "plugins.revoked.security",
                },
            }
        );

    /// <summary>An installed plugin; a null hash is one installed before the hash was kept.</summary>
    private PluginInfo Installed(string version, string? packageHash, string listing = Listing)
    {
        string pluginFolder = Path.Combine(_folder, "plugins", Ulid.NewUlid().ToString());
        Directory.CreateDirectory(pluginFolder);
        File.WriteAllText(Path.Combine(pluginFolder, "plugin.json"), "{}");

        if (packageHash is not null)
            File.WriteAllText(Path.Combine(pluginFolder, ".package.sha256"), packageHash);

        return new()
        {
            Id = Ulid.Parse(listing),
            Name = "Sample",
            Description = "A sample plugin.",
            Version = Version.Parse(version),
            Status = PluginStatus.Active,
            ManifestPath = Path.Combine(pluginFolder, "plugin.json"),
            AssemblyPath = Path.Combine(pluginFolder, "Sample.dll"),
        };
    }

    private PluginRefusal? Decide(string entries, PluginInfo plugin)
    {
        PluginRevocationStore store = new(Path.Combine(_folder, "config"));
        PluginRevocationClient client = new(
            new HttpClient(),
            store,
            new PluginTrustedKeys(new Dictionary<string, string> { ["nomercy-1"] = _publicKey }),
            NullLogger<PluginRevocationClient>.Instance
        );

        client
            .TryRead(Body(entries), out PluginRevocationList? list)
            .Should()
            .BeTrue("a signed list in a shape this server knows is read");
        store.Save(list!);

        // A second store on the same folder: what was saved is what a restart reads.
        PluginRevocationStore reopened = new(Path.Combine(_folder, "config"));

        return new PluginRevocationRunCheck(
            new PluginRevocationGate(reopened, new StubClock(Now)),
            reopened
        ).Check(plugin);
    }

    [Fact]
    public void A_published_entry_revokes_the_installed_package_by_sha256()
    {
        Decide(Published(Listing, "1.2.0", Hash.ToUpperInvariant()), Installed("1.2.0", Hash))!
            .Code.Should()
            .Be(PluginRefusalCodes.Revoked);
    }

    [Fact]
    public void A_published_entry_without_a_hash_revokes_by_version()
    {
        Decide(Published(Listing, "1.2.0", null), Installed("1.2.0", OtherHash))!
            .Code.Should()
            .Be(PluginRefusalCodes.Revoked);
    }

    [Fact]
    public void A_published_entry_without_a_hash_or_a_version_revokes_the_whole_listing()
    {
        Decide(Published(Listing, null, null), Installed("3.4.5", OtherHash))!
            .Code.Should()
            .Be(PluginRefusalCodes.Revoked);
    }

    [Fact]
    public void An_entry_in_the_earlier_shape_still_revokes_by_package_hash()
    {
        Decide(Earlier(Listing, Hash), Installed("1.2.0", Hash))!
            .Code.Should()
            .Be(PluginRefusalCodes.Revoked);
    }

    [Fact]
    public void A_plugin_with_no_stored_hash_is_revoked_by_version()
    {
        Decide(Published(Listing, "1.2.0", Hash), Installed("1.2.0", null))!
            .Code.Should()
            .Be(PluginRefusalCodes.Revoked);
    }

    [Fact]
    public void A_plugin_with_no_stored_hash_is_not_revoked_by_a_hash_it_cannot_be_compared_to()
    {
        Decide(Published(Listing, null, Hash), Installed("1.2.0", null)).Should().BeNull();
    }

    [Fact]
    public void Another_version_of_the_listing_is_not_revoked()
    {
        Decide(Published(Listing, "1.2.0", null), Installed("1.3.0", Hash)).Should().BeNull();
    }

    [Fact]
    public void Another_package_of_the_same_version_is_not_revoked()
    {
        Decide(Published(Listing, "1.2.0", Hash), Installed("1.2.0", OtherHash)).Should().BeNull();
    }

    [Fact]
    public void Another_listing_is_not_revoked()
    {
        Decide(Published(OtherListing, null, null), Installed("1.2.0", Hash)).Should().BeNull();
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
