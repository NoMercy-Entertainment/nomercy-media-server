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

using NoMercy.Service.Hosting;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Service.Hosting;

[Trait("Category", "Unit")]
public sealed class ServerInstanceLockTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nm_lock_{Guid.NewGuid():N}"
    );
    private readonly LocalStorageDriver _storage = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void ASecondServer_IsToldTheLockIsHeld_AfterRetryingBriefly()
    {
        List<TimeSpan> pauses = [];
        using ServerInstanceLock? first = ServerInstanceLock.TryAcquire(_storage, _folder).Lock;

        ServerInstanceLockAttempt second = ServerInstanceLock.TryAcquire(
            _storage,
            _folder,
            pauses.Add
        );

        first.Should().NotBeNull();
        second.Lock.Should().BeNull();
        second.HeldElsewhere.Should().BeTrue();
        pauses.Should().HaveCount(2);
        pauses.Sum(pause => pause.TotalMilliseconds).Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public void TheLock_IsFreeAgainOnceReleased()
    {
        ServerInstanceLock? first = ServerInstanceLock.TryAcquire(_storage, _folder).Lock;
        first!.Dispose();

        using ServerInstanceLock? again = ServerInstanceLock.TryAcquire(_storage, _folder).Lock;

        again.Should().NotBeNull();
    }

    [Fact]
    public void AFolderThatCannotBeWritten_IsReportedAsSuch_NotAsAnotherServer()
    {
        Mock<IStorageDriver> readOnly = new();
        readOnly
            .Setup(storage => storage.OpenWrite(It.IsAny<string>(), It.IsAny<bool>()))
            .Throws(new UnauthorizedAccessException("Access to the path is denied."));
        List<TimeSpan> pauses = [];

        ServerInstanceLockAttempt attempt = ServerInstanceLock.TryAcquire(
            readOnly.Object,
            _folder,
            pauses.Add
        );

        attempt.Lock.Should().BeNull();
        attempt.HeldElsewhere.Should().BeFalse();
        attempt.Problem.Should().Contain("denied");
        pauses.Should().BeEmpty();
    }

    [Fact]
    public void ALockingFailureOfAnotherKind_IsNotTakenForAnotherServer()
    {
        Mock<IStorageDriver> share = new();
        share
            .Setup(storage => storage.OpenWrite(It.IsAny<string>(), It.IsAny<bool>()))
            .Throws(new IOException("The request is not supported.", unchecked((int)0x80070032)));

        ServerInstanceLockAttempt attempt = ServerInstanceLock.TryAcquire(
            share.Object,
            _folder,
            _ => { }
        );

        attempt.HeldElsewhere.Should().BeFalse();
        attempt.Problem.Should().Contain("not supported");
    }
}
