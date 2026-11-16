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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Entitlements;
using NoMercy.PluginSdk.Verification;
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

    // The shape nomercy-tv publishes (EntitlementBundle.php), field for field.
    private const string Entitlements =
        """[{"user_id":"11111111-1111-1111-1111-111111111111","listing_id":"01J9ZK5V8Y0000000000000002","tier":"paid","status":"active","source":"purchase","valid_until":null,"shareable":true,"max_shared_seats":5}]""";

    private string Body(
        string? signedEntitlements = null,
        string keyId = "nomercy-1",
        string serverId = ServerId,
        string entitlements = Entitlements
    )
    {
        JsonElement sent = JsonDocument.Parse(entitlements).RootElement;

        return JsonSerializer.Serialize(
            new
            {
                server_id = serverId,
                issued_at = IssuedAt,
                refresh_by = RefreshBy,
                entitlements = sent,
                signature = new
                {
                    kid = keyId,
                    alg = "ed25519",
                    value = Sign(
                        $"{serverId}{IssuedAt}{RefreshBy}{signedEntitlements ?? JsonSerializer.Serialize(sent)}"
                    ),
                },
            }
        );
    }

    private PluginEntitlementClient Client(IPluginEntitlementStore store, bool trusted = true) =>
        new(
            new HttpClient(),
            store,
            trusted
                ? new PluginTrustedKeys(
                    new Dictionary<string, string> { ["nomercy-1"] = _publicKey }
                )
                : PluginTrustedKeys.None,
            NullLogger<PluginEntitlementClient>.Instance,
            Ulid.Parse(ServerId)
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
    public void An_entitlement_that_names_plugin_id_instead_of_listing_id_is_refused()
    {
        string earlierShape = Entitlements.Replace("\"listing_id\"", "\"plugin_id\"");

        Client(new PluginEntitlementStore(_folder))
            .TryRead(Body(entitlements: earlierShape), out PluginEntitlementBundle? bundle)
            .Should()
            .BeFalse("the published field is listing_id, and nothing else names the plugin");

        bundle.Should().BeNull();
    }

    [Fact]
    public void A_subscription_lapses_at_the_published_valid_until()
    {
        string subscription = Entitlements.Replace(
            "\"valid_until\":null",
            "\"valid_until\":\"2026-10-01T00:00:00+00:00\""
        );

        Client(new PluginEntitlementStore(_folder))
            .TryRead(Body(entitlements: subscription), out PluginEntitlementBundle? bundle)
            .Should()
            .BeTrue();

        DateTimeOffset lapse = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        bundle!.Entitlements.Should().ContainSingle().Which.ExpiresAt.Should().Be(lapse);
        bundle.For(Paid, Owner, lapse.AddSeconds(-1)).Should().NotBeNull();
        bundle.For(Paid, Owner, lapse.AddSeconds(1)).Should().BeNull();
    }

    [Fact]
    public void A_suspended_entitlement_is_read_as_suspended_and_the_gate_refuses_it()
    {
        string suspended = Entitlements.Replace(
            "\"status\":\"active\"",
            "\"status\":\"suspended\""
        );

        PluginEntitlementStore store = new(_folder);

        Client(store).Accept(Body(entitlements: suspended)).Should().BeTrue();

        store
            .Current.Entitlements.Should()
            .ContainSingle()
            .Which.Status.Should()
            .Be(PluginEntitlementStatus.Suspended);

        DateTimeOffset now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        store.Current.For(Paid, Owner, now).Should().BeNull("a paused purchase is not held");

        PluginRefusal? refusal = new PluginEntitlementGate(store, new FixedClock(now), Owner).Check(
            Paid,
            PluginTier.Paid
        );

        refusal.Should().NotBeNull("a suspended entitlement must not start the plugin");
        refusal!.Why.Should().Contain("paused");
    }

    [Fact]
    public void An_active_entitlement_is_read_as_active()
    {
        Client(new PluginEntitlementStore(_folder))
            .TryRead(Body(), out PluginEntitlementBundle? bundle)
            .Should()
            .BeTrue();

        bundle!
            .Entitlements.Should()
            .ContainSingle()
            .Which.Status.Should()
            .Be(PluginEntitlementStatus.Active);
    }

    [Theory]
    [InlineData("\"status\":\"active\",", "")]
    [InlineData("\"status\":\"active\"", "\"status\":\"disputed\"")]
    [InlineData("\"status\":\"active\"", "\"status\":null")]
    public void An_entitlement_without_a_known_status_is_refused(string published, string sent)
    {
        Client(new PluginEntitlementStore(_folder))
            .TryRead(
                Body(entitlements: Entitlements.Replace(published, sent)),
                out PluginEntitlementBundle? bundle
            )
            .Should()
            .BeFalse("a status this server cannot name is not a purchase it can confirm");

        bundle.Should().BeNull();
    }

    [Fact]
    public void A_bundle_saved_before_status_was_read_holds_nothing()
    {
        PluginEntitlementStore store = new(_folder);
        Client(store).Accept(Body()).Should().BeTrue();
        string file = Path.Combine(_folder, "entitlements.json");
        string saved = File.ReadAllText(file);
        string earlier = saved.Replace(",\"status\":1", string.Empty);
        earlier.Should().NotBe(saved, "the saved file names the status");
        File.WriteAllText(file, earlier);

        PluginEntitlementBundle reopened = new PluginEntitlementStore(_folder).Current;

        reopened.Entitlements.Should().ContainSingle();
        reopened
            .For(Paid, Owner, new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero))
            .Should()
            .BeNull("no shim keeps a bundle without a status running a paid plugin");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void A_bundle_naming_the_server_by_its_uuid_is_read()
    {
        // nomercy-tv names a server by the uuid it registered with, not a ulid.
        const string Uuid = "0b6f1c8e-4a5d-4f3e-9c2b-7d1e8a9f0c11";

        Client(new PluginEntitlementStore(_folder))
            .TryRead(Body(serverId: Uuid), out PluginEntitlementBundle? bundle)
            .Should()
            .BeTrue();

        bundle!.ServerId.Should().Be(new Ulid(Guid.Parse(Uuid)));
    }

    [Fact]
    public async Task A_refresh_sends_the_servers_token_when_it_holds_one()
    {
        HeaderRecorder recorder = new(Body());
        PluginEntitlementClient client = new(
            new HttpClient(recorder),
            new RecordingStore(),
            new PluginTrustedKeys(new Dictionary<string, string> { ["nomercy-1"] = _publicKey }),
            NullLogger<PluginEntitlementClient>.Instance
        );

        await client.RefreshAsync(
            new("https://api.nomercy.tv/v1/server/plugins/entitlements?id=x"),
            "the-token"
        );

        recorder.Authorization.Should().Be("Bearer the-token");
    }

    private sealed class HeaderRecorder(string body) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Authorization = request.Headers.Authorization?.ToString();

            return Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body),
                }
            );
        }
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
    public void An_older_signed_bundle_does_not_replace_the_newer_one_Aud1061()
    {
        PluginEntitlementStore store = new(_folder);
        DateTimeOffset newer = new(2026, 9, 25, 11, 0, 0, TimeSpan.Zero);
        store.Save(new(Ulid.Parse(ServerId), newer, newer.AddDays(1), []));

        Client(store).Accept(Body()).Should().BeFalse();

        store.Current.IssuedAt.Should().Be(newer);
        store.Current.Entitlements.Should().BeEmpty();
    }

    [Fact]
    public void A_signed_bundle_for_another_server_is_not_saved()
    {
        PluginEntitlementStore store = new(_folder);
        DateTimeOffset held = new(2026, 9, 19, 11, 0, 0, TimeSpan.Zero);
        store.Save(new(Ulid.Parse(ServerId), held, held.AddDays(1), []));

        Client(store).Accept(Body(serverId: "01J9ZK5V8Y000000000000000Y")).Should().BeFalse();

        store.Current.IssuedAt.Should().Be(held);
        store.Current.Entitlements.Should().BeEmpty();
    }

    [Fact]
    public void A_first_bundle_for_this_server_saves_when_none_is_stored()
    {
        PluginEntitlementStore store = new(_folder);

        Client(store).Accept(Body()).Should().BeTrue();

        store.Current.Entitlements.Should().ContainSingle();
    }

    [Fact]
    public void A_bundle_issued_at_the_same_time_as_the_stored_one_still_saves()
    {
        PluginEntitlementStore store = new(_folder);
        DateTimeOffset same = new(2026, 9, 20, 11, 0, 0, TimeSpan.Zero);
        store.Save(new(Ulid.Parse(ServerId), same, same.AddDays(1), []));

        Client(store).Accept(Body()).Should().BeTrue();

        store.Current.Entitlements.Should().ContainSingle();
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
                [
                    new PluginEntitlement(
                        Paid,
                        Owner,
                        PluginTier.Paid,
                        5,
                        null,
                        PluginEntitlementStatus.Active
                    ),
                ]
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
