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
using NoMercy.PluginSdk.Lan;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The file the owner's devices live in: only hashes of their credentials,
/// a file from before hashing that keeps working, and writers that cannot
/// undo each other.
/// </summary>
public class PluginLanDeviceStoreTests : IDisposable
{
    private static readonly Ulid Iptv = Ulid.Parse("01J9ZK5V8Y00000000000000E1");

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-lan-devices-" + Ulid.NewUlid()
    );

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string FilePath => Path.Combine(_folder, "lan-devices.json");

    [Fact]
    public void The_file_holds_the_hash_and_never_the_credential()
    {
        PluginLanDeviceMinter minter = new(new PluginLanDeviceStore(_folder), TimeProvider.System);

        PluginLanDeviceMinted minted = minter.Mint(Iptv, "Living room TV");

        string file = File.ReadAllText(FilePath);
        file.Should().NotContain(minted.Credential);
        file.Should().Contain(PluginLanDeviceMinter.HashOf(minted.Credential));
    }

    [Fact]
    public void A_device_still_works_after_a_restart()
    {
        PluginLanDeviceMinted minted = new PluginLanDeviceMinter(
            new PluginLanDeviceStore(_folder),
            TimeProvider.System
        ).Mint(Iptv, "Living room TV");

        PluginLanDeviceMinter afterRestart = new(
            new PluginLanDeviceStore(_folder),
            TimeProvider.System
        );

        afterRestart.Resolve(Iptv, minted.DeviceId, minted.Credential).Should().NotBeNull();
        afterRestart.Resolve(Iptv, minted.DeviceId, "not-the-credential").Should().BeNull();
    }

    /// <summary>
    /// A file written before credentials were hashed keeps every device
    /// working, and the credentials leave the disk on the first read.
    /// </summary>
    [Fact]
    public void A_file_from_before_hashing_keeps_working_and_is_hashed_once()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            FilePath,
            $$"""
            [
              {
                "plugin_id": "{{Iptv}}",
                "device_id": "living",
                "name": "Living room TV",
                "credential": "old-plaintext-credential",
                "created_at": "2026-09-20T12:00:00+00:00",
                "revoked": false
              },
              {
                "plugin_id": "{{Iptv}}",
                "device_id": "gone",
                "name": "Old box",
                "credential": "another-old-credential",
                "created_at": "2026-09-21T12:00:00+00:00",
                "revoked": true
              }
            ]
            """
        );

        PluginLanDeviceMinter minter = new(new PluginLanDeviceStore(_folder), TimeProvider.System);

        minter.Resolve(Iptv, "living", "old-plaintext-credential").Should().NotBeNull();
        minter.Resolve(Iptv, "gone", "another-old-credential").Should().BeNull("still revoked");

        string file = File.ReadAllText(FilePath);
        file.Should().NotContain("old-plaintext-credential");
        file.Should().NotContain("another-old-credential");
        file.Should().Contain(PluginLanDeviceMinter.HashOf("old-plaintext-credential"));

        new PluginLanDeviceMinter(new PluginLanDeviceStore(_folder), TimeProvider.System)
            .Resolve(Iptv, "living", "old-plaintext-credential")
            .Should()
            .NotBeNull("the rewritten file still opens for the device");
    }

    [Fact]
    public void A_row_with_no_credential_at_all_matches_nothing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            FilePath,
            $$"""
            [ { "plugin_id": "{{Iptv}}", "device_id": "empty", "name": "?", "created_at": "2026-09-20T12:00:00+00:00", "revoked": false } ]
            """
        );

        PluginLanDeviceStore store = new(_folder);

        store.Find(Iptv, "empty").Should().BeNull();
        new PluginLanDeviceMinter(store, TimeProvider.System)
            .Resolve(Iptv, "empty", string.Empty)
            .Should()
            .BeNull();
    }

    /// <summary>
    /// An addition and a revocation at the same moment each read the rows,
    /// change them and write the file. Neither may undo the other: the
    /// revoked device stays revoked and every added device stays added.
    /// </summary>
    [Fact]
    public async Task Writers_at_the_same_moment_never_undo_each_other()
    {
        PluginLanDeviceStore store = new(_folder);
        PluginLanDeviceMinter minter = new(store, TimeProvider.System);
        List<PluginLanDeviceMinted> revoked = [];

        for (int index = 0; index < 20; index++)
            revoked.Add(minter.Mint(Iptv, $"To revoke {index}"));

        List<Task> writers = [];

        for (int index = 0; index < 20; index++)
        {
            int at = index;
            writers.Add(Task.Run(() => minter.Mint(Iptv, $"Added {at}")));
            writers.Add(Task.Run(() => minter.Revoke(Iptv, revoked[at].DeviceId)));
        }

        await Task.WhenAll(writers);

        PluginLanDeviceStore reread = new(_folder);
        IReadOnlyList<PluginLanDevice> devices = reread.For(Iptv);

        devices.Should().HaveCount(40);
        devices
            .Where(device => device.Name.StartsWith("To revoke"))
            .Should()
            .OnlyContain(device => device.Revoked);
        devices.Where(device => device.Name.StartsWith("Added")).Should().HaveCount(20);
    }
}
