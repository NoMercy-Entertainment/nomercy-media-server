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
using System.Text.Json.Nodes;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Entitlements;
using NoMercy.PluginSdk.Offline;
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
/// A file carried in on a stick answers the same two questions a connected
/// server asks. It is not a way of skipping them, so it is signed, it expires,
/// and a bundle that fails either check saves nothing at all.
/// </summary>
public class PluginOfflineBundleImporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _publicKey;
    private readonly Ed25519PrivateKeyParameters _privateKey;

    public PluginOfflineBundleImporterTests()
    {
        Ed25519KeyPairGenerator generator = new();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

        _privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        _publicKey = Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
    }

    private string Sign(string payload)
    {
        Ed25519Signer signer = new();
        signer.Init(true, _privateKey);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        signer.BlockUpdate(bytes, 0, bytes.Length);

        return Convert.ToBase64String(signer.GenerateSignature());
    }

    private const string Held =
        """{"server_id":"01J9ZK5V8Y000000000000000Z","issued_at":"2026-09-20T11:00:00+00:00","refresh_by":"2026-10-20T11:00:00+00:00","entitlements":[]}""";

    private const string Withdrawn = """{"issued_at":"2026-09-20T11:00:00+00:00","entries":[]}""";

    private string Body(
        double issuedDaysAgo = 1,
        int validDays = 30,
        string? entitlements = null,
        string? revocations = null,
        string? signedEntitlements = null,
        string keyId = "nomercy-1"
    )
    {
        string issuedAt = Now.AddDays(-issuedDaysAgo).ToString("O");

        // Serialized first, then read back, because the signed text is the raw
        // text as it arrives. The serializer escapes the "+" of a timezone
        // offset, so signing the source strings would sign one form and send
        // the other, and every valid bundle would read as forged.
        JsonNode body = JsonNode.Parse(
            JsonSerializer.Serialize(
                new
                {
                    issued_at = issuedAt,
                    valid_days = validDays,
                    entitlements = JsonDocument.Parse(entitlements ?? Held).RootElement,
                    revocations = JsonDocument.Parse(revocations ?? Withdrawn).RootElement,
                }
            )
        )!;

        string entitlementText = signedEntitlements is null
            ? body["entitlements"]!.ToJsonString()
            : JsonNode.Parse(signedEntitlements)!.ToJsonString();

        body["signature"] = new JsonObject
        {
            ["kid"] = keyId,
            ["alg"] = "ed25519",
            ["value"] = Sign(
                $"{issuedAt}{validDays}{entitlementText}{body["revocations"]!.ToJsonString()}"
            ),
        };

        return body.ToJsonString();
    }

    private PluginOfflineBundleImporter Importer(
        RecordingEntitlementStore entitlements,
        RecordingRevocationStore revocations,
        bool trusted = true
    ) =>
        new(
            entitlements,
            revocations,
            trusted
                ? new PluginTrustedKeys(
                    new Dictionary<string, string> { ["nomercy-1"] = _publicKey }
                )
                : PluginTrustedKeys.None,
            new StubClock(Now)
        );

    [Fact]
    public void A_signed_bundle_lands_both_halves()
    {
        RecordingEntitlementStore entitlements = new();
        RecordingRevocationStore revocations = new();

        PluginRefusal? refusal = Importer(entitlements, revocations)
            .Import(new MemoryStream(Encoding.UTF8.GetBytes(Body())));

        refusal.Should().BeNull();
        entitlements.Saved.Should().NotBeNull();
        revocations.Saved.Should().NotBeNull();
    }

    [Fact]
    public void What_lands_is_what_the_bundle_said()
    {
        RecordingEntitlementStore entitlements = new();
        RecordingRevocationStore revocations = new();

        Importer(entitlements, revocations).Import(Body());

        entitlements
            .Saved!.RefreshBy.Should()
            .Be(
                new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.Zero),
                "a bundle that saves an empty one would make every paid plugin dormant"
            );
        revocations
            .Saved!.IssuedAt.Should()
            .Be(new DateTimeOffset(2026, 9, 20, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_bundle_nobody_signed_is_refused_and_nothing_is_saved()
    {
        RecordingEntitlementStore entitlements = new();
        RecordingRevocationStore revocations = new();

        PluginRefusal? refusal = Importer(entitlements, revocations, trusted: false).Import(Body());

        refusal!.Code.Should().Be(PluginRefusalCodes.OfflineBundleInvalid);
        entitlements.Saved.Should().BeNull();
        revocations.Saved.Should().BeNull();
    }

    [Fact]
    public void A_bundle_edited_after_signing_is_refused()
    {
        RecordingEntitlementStore entitlements = new();
        RecordingRevocationStore revocations = new();

        PluginRefusal? refusal = Importer(entitlements, revocations)
            .Import(
                Body(
                    signedEntitlements: """{"server_id":"01J9ZK5V8Y000000000000000Z","issued_at":"2026-09-20T11:00:00+00:00","refresh_by":"2026-09-20T11:00:00+00:00","entitlements":[]}"""
                )
            );

        refusal!.Code.Should().Be(PluginRefusalCodes.OfflineBundleInvalid);
        entitlements.Saved.Should().BeNull();
    }

    [Fact]
    public void A_bundle_past_its_validity_is_refused_and_says_how_long()
    {
        RecordingEntitlementStore entitlements = new();
        RecordingRevocationStore revocations = new();

        PluginRefusal? refusal = Importer(entitlements, revocations)
            .Import(Body(issuedDaysAgo: 31, validDays: 30));

        refusal!.Code.Should().Be(PluginRefusalCodes.OfflineBundleExpired);
        refusal.Why.Should().Contain("30 days");
        refusal.Fix.Should().Contain("device that is online");
        entitlements.Saved.Should().BeNull();
        revocations.Saved.Should().BeNull();
    }

    [Fact]
    public void A_bundle_on_the_last_day_of_its_validity_still_lands()
    {
        RecordingEntitlementStore entitlements = new();

        Importer(entitlements, new()).Import(Body(issuedDaysAgo: 30, validDays: 30));

        entitlements.Saved.Should().NotBeNull("the window is thirty days, so day thirty is inside");
    }

    [Fact]
    public void The_validity_is_the_bundles_own_not_a_number_this_server_chose()
    {
        RecordingEntitlementStore entitlements = new();

        Importer(entitlements, new()).Import(Body(issuedDaysAgo: 40, validDays: 60));

        entitlements
            .Saved.Should()
            .NotBeNull("a bundle issued for sixty days is good for sixty days");
    }

    [Fact]
    public void Something_that_is_not_a_bundle_is_refused_rather_than_throwing()
    {
        RecordingEntitlementStore entitlements = new();

        Importer(entitlements, new())
            .Import("<html>not a bundle</html>")!
            .Code.Should()
            .Be(PluginRefusalCodes.OfflineBundleInvalid);

        entitlements.Saved.Should().BeNull();
    }

    [Fact]
    public void Offline_mode_is_off_until_the_owner_turns_it_on()
    {
        PluginOfflineMode.Off.Enabled.Should().BeFalse();
        PluginOfflineMode.Off.BundleValidityDays.Should().Be(30);
    }

    [Fact]
    public void The_owners_setting_survives_a_restart()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"nm-offline-{Ulid.NewUlid():N}");

        try
        {
            new PluginOfflineMode(true, 14).Save(folder);

            PluginOfflineMode reloaded = PluginOfflineMode.Load(folder);

            reloaded.Enabled.Should().BeTrue();
            reloaded.BundleValidityDays.Should().Be(14);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_setting_file_nobody_can_read_reads_as_off()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"nm-offline-{Ulid.NewUlid():N}");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "offline.json"), "{ not json");

            PluginOfflineMode
                .Load(folder)
                .Enabled.Should()
                .BeFalse("off is the state that asks NoMercy rather than the one that does not");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class RecordingEntitlementStore : IPluginEntitlementStore
    {
        public PluginEntitlementBundle? Saved { get; private set; }

        public PluginEntitlementBundle Current => PluginEntitlementBundle.None;

        public void Save(PluginEntitlementBundle replacement) => Saved = replacement;
    }

    private sealed class RecordingRevocationStore : IPluginRevocationStore
    {
        public PluginRevocationList? Saved { get; private set; }

        public PluginRevocationList Current => PluginRevocationList.None;

        public void Save(PluginRevocationList replacement) => Saved = replacement;
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
