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

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The broker asks three questions in order, because each has a different fix.
/// Collapsed into one, an author adds the line to plugin.json that the message
/// asked for and watches nothing change, because the real answer was that the
/// owner never approved it.
/// </summary>
public class PluginCapabilityBrokerTests
{
    private static readonly Ulid PluginUlid = Ulid.Parse("01J9ZK5V8Y0000000000000000");

    private static PluginCapabilities Declaring(params string[] hooks) =>
        new()
        {
            Hooks = [.. hooks],
            Network = new() { Hosts = ["*.example.com"] },
        };

    private static (PluginCapabilityBroker Broker, PluginRefusalCounter Counter) Build(
        PluginCapabilities? declared,
        bool consented,
        params (string Kind, string Value)[] grants
    )
    {
        PluginRefusalCounter counter = new();
        StubGrantStore grantStore = new(grants);

        PluginCapabilityBroker broker = new(
            new StubManifestSource(PluginUlid, declared),
            new StubConsentService(consented),
            grantStore,
            counter,
            NullLogger<PluginCapabilityBroker>.Instance
        );

        return (broker, counter);
    }

    [Fact]
    public void An_undeclared_capability_refuses_and_names_the_manifest()
    {
        (PluginCapabilityBroker broker, _) = Build(Declaring("network.fetch"), consented: true);

        PluginRefusal? refusal = broker.Check(PluginUlid, "network.dial", "tracker.example");

        refusal.Should().NotBeNull();
        refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
        refusal.Fix.Should().Contain("network.dial").And.Contain("plugin.json");
    }

    [Fact]
    public void Declared_but_not_consented_refuses_and_does_not_name_the_manifest()
    {
        (PluginCapabilityBroker broker, _) = Build(Declaring("network.fetch"), consented: false);

        PluginRefusal? refusal = broker.Check(PluginUlid, "network.fetch", "api.example.com");

        refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotConsented);
        refusal
            .Fix.Should()
            .NotContain(
                "plugin.json",
                "telling an author to edit a line that is already correct wastes their afternoon"
            );
    }

    [Fact]
    public void A_scope_outside_the_declaration_refuses_with_the_scope_code()
    {
        (PluginCapabilityBroker broker, _) = Build(Declaring("network.fetch"), consented: true);

        PluginRefusal? refusal = broker.Check(PluginUlid, "network.fetch", "tracker.example");

        refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityScopeRefused);
        refusal.What.Should().Contain("tracker.example");
    }

    [Fact]
    public void A_scope_inside_the_declaration_is_allowed()
    {
        (PluginCapabilityBroker broker, _) = Build(Declaring("network.fetch"), consented: true);

        broker.Check(PluginUlid, "network.fetch", "api.example.com").Should().BeNull();
    }

    [Fact]
    public void A_scope_granted_after_install_is_allowed_without_the_manifest_naming_it()
    {
        (PluginCapabilityBroker broker, _) = Build(
            Declaring("network.fetch"),
            consented: true,
            (PluginGrantKind.ForCapability("network.fetch"), "tracker.example")
        );

        broker
            .Check(PluginUlid, "network.fetch", "tracker.example")
            .Should()
            .BeNull("the owner saying yes later is the same yes");
    }

    [Fact]
    public void A_capability_with_no_scope_needs_only_the_declaration_and_the_consent()
    {
        (PluginCapabilityBroker broker, _) = Build(Declaring("library.read"), consented: true);

        broker.Check(PluginUlid, "library.read").Should().BeNull();
    }

    [Fact]
    public void A_name_the_vocabulary_does_not_carry_does_not_refuse_the_plugin()
    {
        (PluginCapabilityBroker broker, _) = Build(Declaring("network.fetch"), consented: true);

        broker
            .Check(PluginUlid, "not.a.capability", "anything")
            .Should()
            .BeNull(
                "nothing could have declared it, so refusing sends an author looking for a line that does not exist"
            );
    }

    [Fact]
    public void Every_refusal_is_counted_and_an_allowed_call_is_not()
    {
        (PluginCapabilityBroker broker, PluginRefusalCounter counter) = Build(
            Declaring("network.fetch"),
            consented: true
        );

        broker.Check(PluginUlid, "network.dial", "tracker.example");
        broker.Check(PluginUlid, "network.dial", "tracker.example");
        broker.Check(PluginUlid, "network.fetch", "api.example.com");

        counter
            .Counts(PluginUlid)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(new KeyValuePair<string, int>(PluginRefusalCodes.CapabilityNotDeclared, 2));
    }

    [Fact]
    public void A_plugin_the_host_does_not_know_refuses_rather_than_passing()
    {
        (PluginCapabilityBroker broker, _) = Build(declared: null, consented: true);

        broker
            .Check(PluginUlid, "network.fetch", "api.example.com")
            .Should()
            .NotBeNull("an unknown plugin is the one case where allowing is the dangerous answer");
    }

    private sealed class StubManifestSource(Ulid id, PluginCapabilities? capabilities)
        : IPluginManifestSource
    {
        public PluginInfo? Find(Ulid pluginId) =>
            capabilities is null || pluginId != id
                ? null
                : new()
                {
                    Id = id,
                    Name = "Sample",
                    Description = "d",
                    Version = new(1, 0, 0),
                    Status = PluginStatus.Active,
                    Capabilities = capabilities,
                };

        public IReadOnlyList<PluginInfo> All() => Find(id) is { } only ? [only] : [];
    }

    private sealed class StubConsentService(bool consented) : IPluginConsentService
    {
        public bool IsBaseline(PluginCapabilities? capabilities) => false;

        public bool HasConsent(Ulid pluginId) => consented;

        public bool ConsentCoversCapabilities(
            Ulid pluginId,
            PluginCapabilities? capabilities,
            Version installedVersion
        ) => consented;

        public PluginCapabilities? ConsentedCapabilities(Ulid pluginId) => null;

        private readonly HashSet<string> _approved = new(StringComparer.Ordinal);

        public void ApproveCapability(Ulid pluginId, string capability, Version manifestVersion) =>
            _approved.Add(capability);

        public void RevokeCapability(Ulid pluginId, string capability) =>
            _approved.Remove(capability);

        public bool IsApproved(Ulid pluginId, string capability) => _approved.Contains(capability);

        public Version? ApprovedAt(Ulid pluginId, string capability) =>
            _approved.Contains(capability) ? new Version(1, 0, 0) : null;

        public void GrantConsent(
            Ulid pluginId,
            PluginCapabilities? capabilities,
            Version installedVersion
        ) { }

        public void RevokeConsent(Ulid pluginId) { }
    }

    private sealed class StubGrantStore((string Kind, string Value)[] grants) : IPluginGrantStore
    {
        public IReadOnlyList<string> Granted(Ulid pluginId, string kind) =>
            [.. grants.Where(grant => grant.Kind == kind).Select(grant => grant.Value)];

        public bool Holds(Ulid pluginId, string kind, string value) =>
            grants.Any(grant => grant.Kind == kind && grant.Value == value);

        public void Grant(Ulid pluginId, string kind, string value) { }

        public void Revoke(Ulid pluginId, string kind, string value) { }

        public void RevokeAll(Ulid pluginId) { }

        public void Request(Ulid pluginId, string kind, string value, string reason) { }

        public IReadOnlyList<PluginGrantRequest> PendingRequests() => [];

        public void ClearRequest(Ulid pluginId, string kind, string value) { }
    }
}
