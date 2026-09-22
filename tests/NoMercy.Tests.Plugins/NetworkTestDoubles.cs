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

using System.Runtime.CompilerServices;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Network;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The network, recorded rather than spoken to. What these prove is that the
/// capability checks happen BEFORE anything reaches the wire, which a real
/// socket cannot show: a refusal that arrives after the first multicast has
/// already told the network what the plugin was looking for.
/// </summary>
public sealed class FakeDiscoveryClient : IPluginServiceDiscoveryClient
{
    public List<string> Browsed { get; } = [];
    public List<string> Announced { get; } = [];
    public List<string> Stopped { get; } = [];
    public List<PluginDiscoveredService> Answers { get; } = [];

    public async IAsyncEnumerable<PluginDiscoveredService> BrowseAsync(
        string protocol,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        Browsed.Add(protocol);

        foreach (PluginDiscoveredService answer in Answers)
        {
            ct.ThrowIfCancellationRequested();

            yield return answer;
        }

        await Task.CompletedTask;
    }

    public Task AnnounceAsync(
        string protocol,
        string instance,
        int port,
        IReadOnlyDictionary<string, string>? attributes,
        CancellationToken ct = default
    )
    {
        Announced.Add(instance);

        return Task.CompletedTask;
    }

    public Task StopAsync(string protocol, string instance, CancellationToken ct = default)
    {
        Stopped.Add(instance);

        return Task.CompletedTask;
    }
}

/// <summary>A router that grants what it is told to grant, and remembers.</summary>
public sealed class FakePortMapClient : IPluginPortMapClient
{
    public int? GrantExternalPort { get; set; }
    public List<PluginPortMapping> Mapped { get; } = [];
    public List<PluginPortMapping> Unmapped { get; } = [];

    public Task<int> MapAsync(
        int internalPort,
        int externalPort,
        PluginTransport transport,
        TimeSpan lease,
        CancellationToken ct = default
    )
    {
        int granted = GrantExternalPort ?? externalPort;
        Mapped.Add(new(internalPort, granted, transport, DateTimeOffset.UtcNow.Add(lease)));

        return Task.FromResult(granted);
    }

    public Task UnmapAsync(PluginPortMapping mapping, CancellationToken ct = default)
    {
        Unmapped.Add(mapping);

        return Task.CompletedTask;
    }
}

/// <summary>
/// The same plugin, plus the grants the owner made. Kept beside the network
/// double because the two differ only in whether the grant store answers
/// anything, and a second near-copy is how they drift apart.
/// </summary>
public sealed class GrantingPlugin(
    Ulid id,
    PluginCapabilities? capabilities,
    string kind,
    params string[] granted
) : IPluginManifestSource, IPluginConsentService, IPluginGrantStore
{
    private readonly NetworkPlugin _plugin = new(id, capabilities);

    public PluginInfo? Find(Ulid pluginId) => _plugin.Find(pluginId);

    public IReadOnlyList<PluginInfo> All() => _plugin.All();

    public bool IsBaseline(PluginCapabilities? declared) => false;

    public bool HasConsent(Ulid pluginId) => true;

    public bool ConsentCoversCapabilities(
        Ulid pluginId,
        PluginCapabilities? declared,
        Version installedVersion
    ) => true;

    public PluginCapabilities? ConsentedCapabilities(Ulid pluginId) => null;

    public void ApproveCapability(Ulid pluginId, string capability, Version manifestVersion) { }

    public void RevokeCapability(Ulid pluginId, string capability) { }

    public bool IsApproved(Ulid pluginId, string capability) => true;

    public Version? ApprovedAt(Ulid pluginId, string capability) => new(1, 0, 0);

    public void GrantConsent(
        Ulid pluginId,
        PluginCapabilities? declared,
        Version installedVersion
    ) { }

    public void RevokeConsent(Ulid pluginId) { }

    public IReadOnlyList<string> Granted(Ulid pluginId, string grantKind) =>
        pluginId == id && grantKind == kind ? granted : [];

    public bool Holds(Ulid pluginId, string grantKind, string value) =>
        Granted(pluginId, grantKind).Contains(value);

    public void Grant(Ulid pluginId, string grantKind, string value) { }

    public void Revoke(Ulid pluginId, string grantKind, string value) { }

    public void RevokeAll(Ulid pluginId) { }

    public void Request(Ulid pluginId, string grantKind, string value, string reason) { }

    public IReadOnlyList<PluginGrantRequest> PendingRequests() => [];

    public void ClearRequest(Ulid pluginId, string grantKind, string value) { }
}

/// <summary>
/// A clock a test can push forward. A lease that lapses in production lapses
/// here in one line, which is why renewal is driven by a clock rather than by
/// a timer inside the facade.
/// </summary>
public sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>A plugin whose manifest and consent say exactly what a test needs.</summary>
public sealed class NetworkPlugin(Ulid id, PluginCapabilities? capabilities)
    : IPluginManifestSource,
        IPluginConsentService,
        IPluginGrantStore
{
    public PluginInfo? Find(Ulid pluginId) =>
        pluginId != id
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

    public bool IsBaseline(PluginCapabilities? declared) => false;

    public bool HasConsent(Ulid pluginId) => true;

    public bool ConsentCoversCapabilities(
        Ulid pluginId,
        PluginCapabilities? declared,
        Version installedVersion
    ) => true;

    public PluginCapabilities? ConsentedCapabilities(Ulid pluginId) => null;

    public void ApproveCapability(Ulid pluginId, string capability, Version manifestVersion) { }

    public void RevokeCapability(Ulid pluginId, string capability) { }

    public bool IsApproved(Ulid pluginId, string capability) => true;

    public Version? ApprovedAt(Ulid pluginId, string capability) => new(1, 0, 0);

    public void GrantConsent(
        Ulid pluginId,
        PluginCapabilities? declared,
        Version installedVersion
    ) { }

    public void RevokeConsent(Ulid pluginId) { }

    public IReadOnlyList<string> Granted(Ulid pluginId, string kind) => [];

    public bool Holds(Ulid pluginId, string kind, string value) => false;

    public void Grant(Ulid pluginId, string kind, string value) { }

    public void Revoke(Ulid pluginId, string kind, string value) { }

    public void RevokeAll(Ulid pluginId) { }

    public void Request(Ulid pluginId, string kind, string value, string reason) { }

    public IReadOnlyList<PluginGrantRequest> PendingRequests() => [];

    public void ClearRequest(Ulid pluginId, string kind, string value) { }
}
