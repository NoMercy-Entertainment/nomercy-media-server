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

using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// One physical device, offered once.
/// </summary>
/// <remarks>
/// A device that loses its stored id hellos under a value nothing matches and is
/// written a second row. Both carry a fingerprint, so the picker listed one TV
/// twice under one name, and only the newest was ever registered on the bus —
/// choosing the other sent the cast nowhere and read as casting being broken.
/// Observed live: "Tv in woonkamer" and "Bedroom TV" each held two rows.
/// </remarks>
public class DeviceSupersededRowTests
{
    private static Device Row(string name, string type = "tv")
    {
        return new()
        {
            Id = Ulid.NewUlid(),
            Name = name,
            Type = type,
            Fingerprint = Ulid.NewUlid().ToString(),
            IsActive = true,
        };
    }

    [Fact]
    public void ARowNothingIsConnectedToIsSuperseded()
    {
        Device stale = Row("Tv in woonkamer");

        List<Device> superseded = DeviceStateRepository.SelectSuperseded([stale], _ => false);

        Assert.Equal([stale], superseded);
    }

    [Fact]
    public void ASecondDeviceThatIsGenuinelyConnectedKeepsItsEntry()
    {
        // Two TVs the owner gave the same name are two devices, not a rotation,
        // and the one on the bus must stay in the picker.
        Device connected = Row("Tv in woonkamer");

        List<Device> superseded = DeviceStateRepository.SelectSuperseded(
            [connected],
            id => id == connected.Id
        );

        Assert.Empty(superseded);
    }

    [Fact]
    public void OnlyTheUnreachableRowsAreTakenOut()
    {
        Device connected = Row("Bedroom TV");
        Device stale = Row("Bedroom TV");

        List<Device> superseded = DeviceStateRepository.SelectSuperseded(
            [connected, stale],
            id => id == connected.Id
        );

        Assert.Equal([stale], superseded);
    }

    [Fact]
    public void RetiringLeavesEverythingButThePickerEntry()
    {
        // The row is not deleted: the custom name and the stored volume are the
        // owner's settings, and losing them to a reinstall would be its own bug.
        Device stale = Row("Bedroom TV");
        stale.CustomName = "Bedroom TV";
        stale.VolumePercent = 42;

        DeviceStateRepository.Retire(stale);

        // GetDevices filters on Fingerprint != null, so this is what removes it.
        Assert.Null(stale.Fingerprint);
        Assert.False(stale.IsActive);
        Assert.Equal("Bedroom TV", stale.CustomName);
        Assert.Equal(42, stale.VolumePercent);
    }

    [Fact]
    public void RowsShowingTheSameNameAreTheSameDeviceEvenWhenTheirRawNamesDiffer()
    {
        Guid owner = Guid.NewGuid();

        // What one physical TV actually looks like after it is written twice: the older row
        // carries the model it registered under and was renamed by the user, the newer one
        // hellos with the name it was given. The picker draws "Tv in woonkamer" for both.
        Device legacy = Row("Nokia Streaming Box 8010");
        legacy.CustomName = "Tv in woonkamer";
        legacy.OwnerUserId = owner;

        Device current = Row("Tv in woonkamer");
        current.OwnerUserId = owner;

        Func<Device, bool> matches = DeviceStateRepository
            .SupersededCandidateFilter(current, owner)
            .Compile();

        Assert.True(matches(legacy));
    }

    [Fact]
    public void ADeviceIsNeverItsOwnSupersededRow()
    {
        Guid owner = Guid.NewGuid();
        Device device = Row("Tv in woonkamer");
        device.OwnerUserId = owner;

        Func<Device, bool> matches = DeviceStateRepository
            .SupersededCandidateFilter(device, owner)
            .Compile();

        Assert.False(matches(device));
    }

    [Fact]
    public void AnotherUsersRowIsNeverSuperseded()
    {
        Guid owner = Guid.NewGuid();
        Device mine = Row("Tv in woonkamer");
        mine.OwnerUserId = owner;

        Device theirs = Row("Tv in woonkamer");
        theirs.OwnerUserId = Guid.NewGuid();

        Func<Device, bool> matches = DeviceStateRepository
            .SupersededCandidateFilter(mine, owner)
            .Compile();

        Assert.False(matches(theirs));
    }

    [Fact]
    public void ADifferentNameIsADifferentDevice()
    {
        Guid owner = Guid.NewGuid();
        Device livingRoom = Row("Tv in woonkamer");
        livingRoom.OwnerUserId = owner;

        Device bedroom = Row("Bedroom TV");
        bedroom.OwnerUserId = owner;

        Func<Device, bool> matches = DeviceStateRepository
            .SupersededCandidateFilter(livingRoom, owner)
            .Compile();

        Assert.False(matches(bedroom));
    }

    [Fact]
    public void ARetiredRowIsNotSupersededAgain()
    {
        Guid owner = Guid.NewGuid();
        Device current = Row("Tv in woonkamer");
        current.OwnerUserId = owner;

        Device alreadyRetired = Row("Tv in woonkamer");
        alreadyRetired.OwnerUserId = owner;
        alreadyRetired.Fingerprint = null;

        Func<Device, bool> matches = DeviceStateRepository
            .SupersededCandidateFilter(current, owner)
            .Compile();

        Assert.False(matches(alreadyRetired));
    }
}
