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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Entitlements;
using NoMercy.Plugins.Verification;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The bundle decides whether a paid plugin runs, so a forged one takes away
/// something the owner paid for. A bundle that does not verify is discarded
/// and the one already here is kept, push and fetch alike.
/// </summary>
public class PluginEntitlementTransportTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nm-entitlements-{Ulid.NewUlid():N}"
    );

    private static readonly Ulid Paid = Ulid.Parse("01J9ZK5V8Y0000000000000002");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string _publicKey;
    private readonly Ed25519PrivateKeyParameters _privateKey;

    public PluginEntitlementTransportTests()
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

    private const string ServerId = "01J9ZK5V8Y000000000000000Z";
    private const string IssuedAt = "2026-09-20T11:00:00+00:00";
    private const string RefreshBy = "2026-09-21T11:00:00+00:00";

    private const string Entitlements =
        """[{"plugin_id":"01J9ZK5V8Y0000000000000002","user_id":"11111111-1111-1111-1111-111111111111","tier":"paid","seats":5,"expires_at":null}]""";

    private string Body(string? signedEntitlements = null, string keyId = "nomercy-1") =>
        JsonSerializer.Serialize(
            new
            {
                server_id = ServerId,
                issued_at = IssuedAt,
                refresh_by = RefreshBy,
                entitlements = JsonDocument.Parse(Entitlements).RootElement,
                signature = new
                {
                    kid = keyId,
                    alg = "ed25519",
                    value = Sign(
                        $"{ServerId}{IssuedAt}{RefreshBy}{signedEntitlements ?? Entitlements}"
                    ),
                },
            }
        );

    private PluginEntitlementClient Client(IPluginEntitlementStore store, bool trusted = true) =>
        new(
            new HttpClient(),
            store,
            trusted
                ? new PluginTrustedKeys(
                    new Dictionary<string, string> { ["nomercy-1"] = _publicKey }
                )
                : PluginTrustedKeys.None,
            NullLogger<PluginEntitlementClient>.Instance
        );

    [Fact]
    public void A_signed_bundle_is_read_whole()
    {
        Client(new PluginEntitlementStore(_folder))
            .TryRead(Body(), out PluginEntitlementBundle? bundle)
            .Should()
            .BeTrue();

        PluginEntitlement held = bundle!.Entitlements.Should().ContainSingle().Subject;
        held.PluginId.Should().Be(Paid);
        held.UserId.Should().Be(Owner);
        held.Tier.Should().Be(PluginTier.Paid);
        held.Seats.Should().Be(5);
        held.ExpiresAt.Should().BeNull();
        bundle.RefreshBy.Should().Be(new DateTimeOffset(2026, 9, 21, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_bundle_whose_entitlements_changed_after_signing_is_rejected()
    {
        Client(new PluginEntitlementStore(_folder))
            .TryRead(Body(signedEntitlements: "[]"), out PluginEntitlementBundle? bundle)
            .Should()
            .BeFalse("an added entitlement is a paid plugin nobody paid for");

        bundle.Should().BeNull();
    }

    [Fact]
    public void A_bundle_whose_refresh_date_changed_after_signing_is_rejected()
    {
        Client(new PluginEntitlementStore(_folder))
            .TryRead(Body().Replace("2026-09-21T11", "2026-10-21T11"), out _)
            .Should()
            .BeFalse("a later refresh date is how an expired bundle is made to look current");
    }

    [Fact]
    public void A_bundle_meant_for_another_server_is_rejected()
    {
        Client(new PluginEntitlementStore(_folder))
            .TryRead(Body().Replace(ServerId, "01J9ZK5V8Y000000000000000Y"), out _)
            .Should()
            .BeFalse("one server's purchases are not another's");
    }

    [Fact]
    public void A_bundle_signed_by_a_key_this_server_does_not_hold_is_rejected()
    {
        Client(new PluginEntitlementStore(_folder), trusted: false)
            .TryRead(Body(), out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void A_push_that_does_not_verify_leaves_the_bundle_already_here_alone()
    {
        RecordingStore store = new();

        Client(store, trusted: false).Accept(Body()).Should().BeFalse();

        store.Saved.Should().BeFalse();
    }

    [Fact]
    public void A_push_that_verifies_lands_the_same_way_a_fetch_does()
    {
        RecordingStore store = new();

        Client(store).Accept(Body()).Should().BeTrue();

        store.Saved.Should().BeTrue();
    }

    [Fact]
    public void A_saved_bundle_is_read_back_by_a_server_that_starts_offline()
    {
        PluginEntitlementStore written = new(_folder);
        written.Save(
            new(
                Ulid.Parse(ServerId),
                new DateTimeOffset(2026, 9, 20, 11, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 21, 11, 0, 0, TimeSpan.Zero),
                [new PluginEntitlement(Paid, Owner, PluginTier.Paid, 5, null)]
            )
        );

        PluginEntitlementBundle reopened = new PluginEntitlementStore(_folder).Current;

        reopened
            .For(Paid, Owner, new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero))
            .Should()
            .NotBeNull();
        reopened.RefreshBy.Should().Be(new DateTimeOffset(2026, 9, 21, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_server_with_no_file_has_never_asked()
    {
        new PluginEntitlementStore(_folder).Current.RefreshBy.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public void A_file_that_cannot_be_read_has_never_asked_rather_than_owning_nothing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "entitlements.json"), "{ not json");

        PluginEntitlementBundle unreadable = new PluginEntitlementStore(_folder).Current;

        unreadable.Entitlements.Should().BeEmpty();
        unreadable
            .RefreshBy.Should()
            .Be(
                DateTimeOffset.MinValue,
                "overdue is dormant, and dormant deletes nothing; a current empty bundle would read as unbought"
            );
    }

    private sealed class RecordingStore : IPluginEntitlementStore
    {
        public bool Saved { get; private set; }

        public PluginEntitlementBundle Current => PluginEntitlementBundle.None;

        public void Save(PluginEntitlementBundle replacement) => Saved = true;
    }
}
