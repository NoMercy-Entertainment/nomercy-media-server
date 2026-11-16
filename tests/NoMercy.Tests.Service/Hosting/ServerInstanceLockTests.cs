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
    public void ASecondServer_CannotTakeTheLockWhileTheFirstHoldsIt()
    {
        using ServerInstanceLock? first = ServerInstanceLock.TryAcquire(_storage, _folder);
        using ServerInstanceLock? second = ServerInstanceLock.TryAcquire(_storage, _folder);

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void TheLock_IsFreeAgainOnceReleased()
    {
        ServerInstanceLock? first = ServerInstanceLock.TryAcquire(_storage, _folder);
        first!.Dispose();

        using ServerInstanceLock? again = ServerInstanceLock.TryAcquire(_storage, _folder);

        again.Should().NotBeNull();
    }
}
