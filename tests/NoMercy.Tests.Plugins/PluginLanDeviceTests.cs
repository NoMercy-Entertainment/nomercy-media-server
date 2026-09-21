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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Lan;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The one plugin route without a bearer, because a tuner cannot hold one.
/// Everything that protects it is here: a credential nobody can guess, bound
/// to one device and one plugin, and gone the moment the owner says so.
/// </summary>
public class PluginLanDeviceTests
{
    private static readonly Ulid Iptv = Ulid.Parse("01J9ZK5V8Y0000000000000010");
    private static readonly Ulid Other = Ulid.Parse("01J9ZK5V8Y0000000000000011");
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static (PluginLanDeviceMinter Minter, InMemoryStore Store) Build()
    {
        InMemoryStore store = new();

        return (new(store, new StubClock(Noon)), store);
    }

    [Fact]
    public void Minting_a_device_returns_a_credential_that_reads_back()
    {
        (PluginLanDeviceMinter minter, _) = Build();

        PluginLanDevice device = minter.Mint(Iptv, "Living room TV");

        minter.Resolve(Iptv, device.DeviceId, device.Credential).Should().NotBeNull();
    }

    [Fact]
    public void An_unknown_credential_is_refused()
    {
        (PluginLanDeviceMinter minter, _) = Build();
        PluginLanDevice device = minter.Mint(Iptv, "Living room TV");

        minter
            .Refuse(Iptv, device.DeviceId, "not-the-credential")!
            .Code.Should()
            .Be(PluginRefusalCodes.LanCredentialInvalid);
    }

    [Fact]
    public void An_unknown_device_is_refused()
    {
        (PluginLanDeviceMinter minter, _) = Build();
        PluginLanDevice device = minter.Mint(Iptv, "Living room TV");

        minter.Refuse(Iptv, "never-added", device.Credential).Should().NotBeNull();
    }

    [Fact]
    public void A_revoked_credential_stops_working_immediately()
    {
        (PluginLanDeviceMinter minter, InMemoryStore store) = Build();
        PluginLanDevice device = minter.Mint(Iptv, "Living room TV");

        store.Revoke(Iptv, device.DeviceId);

        minter
            .Refuse(Iptv, device.DeviceId, device.Credential)!
            .Code.Should()
            .Be(PluginRefusalCodes.LanCredentialInvalid);
    }

    [Fact]
    public void A_credential_from_one_plugin_does_not_open_another()
    {
        (PluginLanDeviceMinter minter, _) = Build();
        PluginLanDevice device = minter.Mint(Iptv, "Living room TV");

        minter.Refuse(Other, device.DeviceId, device.Credential).Should().NotBeNull();
    }

    [Fact]
    public void A_refusal_tells_the_owner_what_to_do_rather_than_the_device()
    {
        (PluginLanDeviceMinter minter, _) = Build();

        PluginRefusal refusal = minter.Refuse(Iptv, "never-added", "nothing")!;

        refusal.Fix.Should().Contain("Add the device again");
        refusal.Severity.Should().Be(PluginRefusalSeverity.Blocked);
    }

    [Fact]
    public void The_credential_is_long_enough_to_not_be_guessed()
    {
        (PluginLanDeviceMinter minter, _) = Build();

        minter
            .Mint(Iptv, "Living room TV")
            .Credential.Length.Should()
            .BeGreaterThanOrEqualTo(43, "nothing else guards this route");
    }

    [Fact]
    public void Two_devices_never_get_the_same_credential()
    {
        (PluginLanDeviceMinter minter, _) = Build();

        minter
            .Mint(Iptv, "Living room TV")
            .Credential.Should()
            .NotBe(minter.Mint(Iptv, "Bedroom TV").Credential);
    }

    [Fact]
    public void The_device_row_remembers_a_device_and_never_a_person()
    {
        (PluginLanDeviceMinter minter, _) = Build();

        PluginLanDevice device = minter.Mint(Iptv, "Living room TV");

        device.Name.Should().Be("Living room TV");
        typeof(PluginLanDevice)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo(
                ["PluginId", "DeviceId", "Name", "Credential", "CreatedAt", "Revoked"],
                "a row that carried a user id would make a tuner a record of who watched"
            );
    }

    private sealed class InMemoryStore : IPluginLanDeviceStore
    {
        private readonly List<PluginLanDevice> _devices = [];

        public void Add(PluginLanDevice device) => _devices.Add(device);

        public PluginLanDevice? Find(Ulid pluginId, string deviceId) =>
            _devices.FirstOrDefault(device =>
                device.PluginId == pluginId && device.DeviceId == deviceId
            );

        public IReadOnlyList<PluginLanDevice> For(Ulid pluginId) =>
            [.. _devices.Where(device => device.PluginId == pluginId)];

        public void Revoke(Ulid pluginId, string deviceId)
        {
            int index = _devices.FindIndex(device =>
                device.PluginId == pluginId && device.DeviceId == deviceId
            );

            if (index >= 0)
                _devices[index] = _devices[index] with { Revoked = true };
        }
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
