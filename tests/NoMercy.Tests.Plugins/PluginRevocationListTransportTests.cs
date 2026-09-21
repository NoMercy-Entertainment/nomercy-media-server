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
using NoMercy.Plugins.Revocation;
using NoMercy.Plugins.Verification;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The list decides whether every plugin on the server runs, so the answer to
/// "who sent this" has to come from the signature and nothing else. A list
/// that does not verify is discarded and the old one kept: otherwise anyone
/// who can answer for that address can pause a server, or tell it nothing was
/// revoked when something was.
/// </summary>
public class PluginRevocationListTransportTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nm-revocations-{Ulid.NewUlid():N}"
    );

    private static readonly Ulid Torrent = Ulid.Parse("01J9ZK5V8Y0000000000000001");
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly string _publicKey;
    private readonly Ed25519PrivateKeyParameters _privateKey;

    public PluginRevocationListTransportTests()
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

    private string Body(
        string issuedAt = "2026-09-20T12:00:00+00:00",
        string entries =
            """[{"plugin_id":"01J9ZK5V8Y0000000000000001","hash":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","reason":"plugins.revoked.security"}]""",
        string? signedEntries = null,
        string keyId = "nomercy-1",
        string algorithm = "ed25519"
    ) =>
        JsonSerializer.Serialize(
            new
            {
                issued_at = issuedAt,
                entries = JsonDocument.Parse(entries).RootElement,
                signature = new
                {
                    kid = keyId,
                    alg = algorithm,
                    value = Sign($"{issuedAt}{signedEntries ?? entries}"),
                },
            }
        );

    private PluginRevocationClient Client(IPluginRevocationStore store, bool trusted = true) =>
        new(
            new HttpClient(),
            store,
            trusted
                ? new PluginTrustedKeys(
                    new Dictionary<string, string> { ["nomercy-1"] = _publicKey }
                )
                : PluginTrustedKeys.None,
            NullLogger<PluginRevocationClient>.Instance
        );

    [Fact]
    public void A_signed_list_is_read()
    {
        Client(new PluginRevocationStore(_folder))
            .TryRead(Body(), out PluginRevocationList? list)
            .Should()
            .BeTrue();

        list!.Entries.Should().ContainSingle();
        list.Find(Torrent, Hash)!.Reason.Should().Be("plugins.revoked.security");
        list.IssuedAt.Should().Be(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_list_whose_entries_changed_after_signing_is_rejected()
    {
        Client(new PluginRevocationStore(_folder))
            .TryRead(Body(signedEntries: "[]"), out PluginRevocationList? list)
            .Should()
            .BeFalse("an added entry pauses a plugin nobody revoked");

        list.Should().BeNull();
    }

    [Fact]
    public void A_list_whose_issue_time_changed_after_signing_is_rejected()
    {
        // Only the date: the serializer escapes the "+" of the offset, so a
        // replace of the whole timestamp would silently match nothing and the
        // test would pass by never having changed anything.
        string body = Body().Replace("2026-09-20T12", "2026-09-21T12");

        Client(new PluginRevocationStore(_folder))
            .TryRead(body, out PluginRevocationList? _)
            .Should()
            .BeFalse("a fresher date on an old list is how a stale server is made to look current");
    }

    [Fact]
    public void A_list_signed_by_a_key_this_server_does_not_hold_is_rejected()
    {
        Client(new PluginRevocationStore(_folder), trusted: false)
            .TryRead(Body(), out PluginRevocationList? _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void A_list_naming_an_algorithm_this_server_does_not_read_is_rejected()
    {
        Client(new PluginRevocationStore(_folder))
            .TryRead(Body(algorithm: "rsa-pss"), out PluginRevocationList? _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void A_list_with_no_signature_at_all_is_rejected()
    {
        Client(new PluginRevocationStore(_folder))
            .TryRead("""{"issued_at":"2026-09-20T12:00:00+00:00","entries":[]}""", out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Something_that_is_not_json_is_rejected_rather_than_throwing()
    {
        Client(new PluginRevocationStore(_folder))
            .TryRead("<html>404</html>", out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void A_saved_list_is_read_back_by_a_server_that_starts_offline()
    {
        PluginRevocationStore written = new(_folder);
        written.Save(
            new(
                new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
                [new PluginRevocationEntry(Torrent, Hash, "plugins.revoked.security")]
            )
        );

        PluginRevocationList reopened = new PluginRevocationStore(_folder).Current;

        reopened.Find(Torrent, Hash).Should().NotBeNull();
        reopened.IssuedAt.Should().Be(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_server_with_no_file_has_never_heard()
    {
        new PluginRevocationStore(_folder).Current.IssuedAt.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public void A_file_that_cannot_be_read_has_never_heard_rather_than_naming_nothing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "revocations.json"), "{ not json");

        new PluginRevocationStore(_folder)
            .Current.IssuedAt.Should()
            .Be(
                DateTimeOffset.MinValue,
                "an empty list would say nothing is revoked on the strength of a corrupt file"
            );
    }
}
