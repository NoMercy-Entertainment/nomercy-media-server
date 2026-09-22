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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// Settings and per-user data, as the plugin process sees them.
/// </summary>
[Trait("Category", "Unit")]
public class RemoteSettingsTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    /// <summary>
    /// The type a plugin asks for lives in the plugin's own assembly, which the
    /// server does not load. The value crosses as JSON and becomes that type
    /// here.
    /// </summary>
    [Fact]
    public void ASettingIsReadIntoThePluginsOwnType()
    {
        RemoteSettings settings = new(
            PluginId,
            new RemoteCall(
                PluginId,
                new CountingBroker(PluginCallResponse.Value("""{"station":"r4","volume":7}"""))
            )
        );

        Radio? radio = settings.Get<Radio>("radio");

        radio.Should().NotBeNull();
        radio!.Station.Should().Be("r4");
        radio.Volume.Should().Be(7);
    }

    /// <summary>
    /// The server cannot call into a plugin process yet, so this event would
    /// never fire. A plugin waiting on it would sit there looking healthy
    /// while the owner changed a setting and nothing happened.
    /// </summary>
    [Fact]
    public void SubscribingToSettingsChangedRefusesRatherThanNeverFiring()
    {
        RemoteSettings settings = new(
            PluginId,
            new RemoteCall(PluginId, new CountingBroker(PluginCallResponse.Value("null")))
        );

        Assert.Throws<PluginRefusedException>(() => settings.SettingsChanged += (_, _) => { });
    }

    /// <summary>
    /// A watch list is what a person is changing while the plugin runs, so a
    /// cached one is a plugin showing a resume point the viewer has passed.
    /// </summary>
    [Fact]
    public async Task NothingIsRememberedBetweenUserReads()
    {
        CountingBroker broker = new(PluginCallResponse.Value("[]"));
        RemoteUserData data = new(new RemoteCall(PluginId, broker));

        await data.WatchAsync();
        await data.WatchAsync();

        broker.Calls.Should().Be(2);
    }

    /// <summary>
    /// An identity the server could not name is not an empty person. Inventing
    /// one would hand the plugin a user who does not exist, and everything it
    /// saved afterwards would be saved against nobody.
    /// </summary>
    [Fact]
    public async Task AnIdentityTheServerCouldNotNameIsRefusedRatherThanInvented()
    {
        RemoteUserData data = new(
            new RemoteCall(PluginId, new CountingBroker(PluginCallResponse.Value("null")))
        );

        await Assert.ThrowsAsync<PluginRefusedException>(() => data.IdentityAsync());
    }
}

file sealed record Radio(string Station, int Volume);
