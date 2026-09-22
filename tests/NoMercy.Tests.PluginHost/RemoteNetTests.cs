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
using NoMercy.PluginHost;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Ipc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// Sockets the server permitted and the plugin opens.
/// <para>
/// What matters here is that nothing is dialed before the server has said yes,
/// and that the server is asked every time rather than once at startup.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class RemoteNetTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    /// <summary>
    /// A refused dial must not open a socket. Asking afterwards would be a
    /// plugin already connected to a host the owner refused.
    /// </summary>
    [Fact]
    public async Task ARefusedHostIsNeverDialed()
    {
        CountingBroker broker = new(
            PluginCallResponse.Refused(
                new WireRefusal(
                    PluginRefusalCodes.HostNotAllowed,
                    PluginId.ToString(),
                    "The plugin reached radio.example, which none of its granted host globs match.",
                    "The owner consented to a list of hosts.",
                    "Add the host to the manifest. Docs: /nomercy-plugins/capabilities/network-dial",
                    PluginRefusalSeverity.Blocked.ToString()
                )
            )
        );

        RemoteNet net = new(PluginId, new RemoteCall(PluginId, broker));

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            net.DialAsync("radio.example", 8000, PluginTransport.Tcp)
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.HostNotAllowed);
        broker.Calls.Should().Be(1);
    }

    /// <summary>
    /// The owner can withdraw a host while the plugin is running, so a plugin
    /// that cached permission would keep reaching a host since refused.
    /// </summary>
    [Fact]
    public async Task TheServerIsAskedOnEveryDialRatherThanOnceAtStartup()
    {
        CountingBroker broker = new(PluginCallResponse.Value("true"));
        RemoteNet net = new(PluginId, new RemoteCall(PluginId, broker));

        // Nothing is listening, so the connection fails after the permission
        // was granted. The permission is what this measures.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await net.DialAsync("127.0.0.1", 1, PluginTransport.Tcp);
            }
            catch (Exception exception) when (exception is not PluginRefusedException)
            {
                // A closed port is not a refusal.
            }
        }

        broker.Calls.Should().Be(2);
    }

    /// <summary>
    /// Listening, discovery and port mapping do not cross yet. A facade that
    /// answered a listener nobody could reach would look to a plugin like a
    /// port the owner's router had closed.
    /// </summary>
    [Fact]
    public void AMemberThatDoesNotCrossYetSaysSoRatherThanAnsweringEmpty()
    {
        RemoteNet net = new(
            PluginId,
            new RemoteCall(PluginId, new CountingBroker(PluginCallResponse.Value("true")))
        );

        Assert.Throws<PluginRefusedException>(() => net.Discovery);
        Assert.Throws<PluginRefusedException>(() => net.PortMap);
    }
}
