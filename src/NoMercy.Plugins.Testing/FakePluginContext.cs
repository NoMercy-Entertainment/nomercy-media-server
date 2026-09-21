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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Testing;

/// <summary>
/// A context a plugin can be driven through in a unit test.
/// <para>
/// Every facade it offers asks the same two questions the host asks before it
/// does anything: was the capability declared, and does the grant cover this
/// particular thing. A harness that answered yes to everything would let a
/// plugin pass its own tests and refuse on the first server it met.
/// </para>
/// <para>
/// Facades with no fake are left to the contract's own default, which refuses
/// and names the facade. That is deliberate: a test that reaches one gets the
/// same message an author would, rather than a null reference.
/// </para>
/// </summary>
public sealed class FakePluginContext : IPluginContext
{
    private readonly PluginRefusalRecorder _recorder = new();
    private readonly FakeGrantSet _grants = new();

    public FakePluginContext(string plugin)
    {
        PluginName = plugin;
        Net = new FakePluginNet(plugin, _grants, _recorder);
        Events = new NullPluginEvents(Published);
    }

    public string PluginName { get; }

    public IReadOnlyList<PluginRefusal> Refusals => _recorder.Refusals;

    /// <summary>
    /// Grants a capability. With no scopes it covers everything of its kind;
    /// with scopes it covers only those, which is the host's own rule.
    /// </summary>
    public FakePluginContext Grant(string capability, params string[] scopes)
    {
        _grants.Grant(capability, scopes);
        return this;
    }

    public FakePluginContext Revoke(string capability)
    {
        _grants.Revoke(capability);
        return this;
    }

    public IPluginNet Net { get; }

    public IPluginEvents Events { get; }
    public ILogger Logger { get; } = NullLogger.Instance;
    public string DataFolderPath { get; init; } = Path.Combine(Path.GetTempPath(), "nomercy-fake");
    public IPluginConfiguration Configuration { get; init; } = null!;
    public HttpClient HttpClient { get; } = new();
    public Ulid PluginId { get; init; } = Ulid.NewUlid();
    public IPluginSecretStore Secrets { get; init; } = null!;
    public IPluginLibraryQuery Library { get; init; } = null!;
    public IPluginLibraryWriter? LibraryWriter => null;
    public IPluginGrants Grants { get; init; } = null!;
    public IPluginHubContext Hub { get; init; } = null!;

    /// <summary>
    /// The plugin's own events. Recorded rather than dropped, so a test can
    /// assert a plugin announced what it did: an event nothing publishes looks
    /// exactly like an event nothing subscribed to.
    /// </summary>
    public List<string> Published { get; } = [];

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default)
    {
        Published.Add(name);
        return Task.CompletedTask;
    }

    private sealed class NullPluginEvents(List<string> published) : IPluginEvents
    {
        public void Subscribe<T>(string topic, Func<T, CancellationToken, Task> handler) { }

        public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default)
        {
            published.Add(name);
            return Task.CompletedTask;
        }
    }
}
