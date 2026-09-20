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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Testing;

/// <summary>
/// Sockets that never leave the process, gated by the same two questions the
/// host asks: was the capability declared, and does the grant cover this.
/// </summary>
public sealed class FakePluginNet(
    string plugin,
    FakeGrantSet grants,
    PluginRefusalRecorder recorder
) : IPluginNet
{
    /// <summary>What was dialed, so a test can assert the plugin reached the host it meant to.</summary>
    public List<string> Dialed { get; } = [];

    public Task<Stream> DialAsync(
        string host,
        int port,
        PluginTransport transport,
        CancellationToken ct = default
    )
    {
        Check(PluginCapabilityNames.NetworkDial, host, $"The plugin dialed {host}:{port}.");
        Dialed.Add($"{host}:{port}");

        return Task.FromResult<Stream>(new MemoryStream());
    }

    public Task<IPluginListener> ListenAsync(
        int port,
        PluginTransport transport,
        CancellationToken ct = default
    )
    {
        Check(
            PluginCapabilityNames.NetworkListen,
            port.ToString(),
            $"The plugin listened on port {port}."
        );

        throw new NotSupportedException(
            "A fake listener would need a fake peer to be worth anything. Assert on the refusal instead."
        );
    }

    public IPluginNetDiscovery Discovery =>
        throw recorder.Raise(
            PluginRefusalMessages.CapabilityNotDeclared(
                plugin,
                PluginCapabilityNames.NetworkDiscover,
                "The plugin used discovery, which the test harness does not fake."
            )
        );

    public IPluginPortMap PortMap =>
        throw recorder.Raise(
            PluginRefusalMessages.CapabilityNotDeclared(
                plugin,
                PluginCapabilityNames.NetworkDiscover,
                "The plugin used port mapping, which the test harness does not fake."
            )
        );

    private void Check(string capability, string scope, string what)
    {
        if (!grants.IsDeclared(capability))
            throw recorder.Raise(
                PluginRefusalMessages.CapabilityNotDeclared(plugin, capability, what)
            );

        if (!grants.Covers(capability, scope))
            throw recorder.Raise(
                PluginRefusalMessages.CapabilityScopeRefused(plugin, capability, scope)
            );
    }
}
