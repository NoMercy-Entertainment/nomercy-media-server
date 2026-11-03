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

using NoMercy.Storage.Drivers.Nfs;
using NoMercy.Storage.Drivers.Nfs.Interop;
using NoMercy.Tests.Storage.Faults;

namespace NoMercy.Tests.Storage;

[Trait("Category", "Unit")]
public sealed class NfsWriteStreamTests
{
    [Fact]
    public void Write_throws_when_libnfs_returns_zero_for_a_nonempty_chunk()
    {
        FaultyLibNfs fake = new();
        fake.Faults["Write:0"] = (0, string.Empty);
        IntPtr ctx = fake.InitContext();
        fake.Seed("/file.bin", []);
        fake.Open(ctx, "/file.bin", LibNfs.O_WRONLY, out IntPtr fh);
        using SemaphoreSlim lockObj = new(1, 1);
        using NfsWriteStream stream = new(ctx, fh, lockObj, fake);

        Action act = () => stream.Write([1, 2, 3], 0, 3);

        act.Should().Throw<IOException>().WithMessage("*zero bytes*");
        fake.CallCounts[nameof(FaultyLibNfs.Write)].Should().Be(1);
    }

    [Fact]
    public void Dispose_throws_when_libnfs_close_fails_and_still_releases_the_driver_lock()
    {
        FaultyLibNfs fake = new();
        fake.Faults["Close:0"] = (-5, "NFS4ERR_DQUOT");
        IntPtr ctx = fake.InitContext();
        fake.Seed("/file.bin", []);
        fake.Open(ctx, "/file.bin", LibNfs.O_WRONLY, out IntPtr fh);
        using SemaphoreSlim lockObj = new(1, 1);
        NfsWriteStream stream = new(ctx, fh, lockObj, fake);
        stream.Write([1, 2, 3], 0, 3);

        Action act = () => stream.Dispose();

        act.Should().Throw<IOException>().WithMessage("*NFS close failed*NFS4ERR_DQUOT*");
        lockObj.CurrentCount.Should().Be(1);
        Action write = () => stream.Write([4], 0, 1);
        write.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_after_a_failed_close_does_not_close_or_throw_a_second_time()
    {
        FaultyLibNfs fake = new();
        fake.Faults["Close:0"] = (-5, "NFS4ERR_DQUOT");
        IntPtr ctx = fake.InitContext();
        fake.Seed("/file.bin", []);
        fake.Open(ctx, "/file.bin", LibNfs.O_WRONLY, out IntPtr fh);
        using SemaphoreSlim lockObj = new(1, 1);
        NfsWriteStream stream = new(ctx, fh, lockObj, fake);
        Action first = () => stream.Dispose();
        first.Should().Throw<IOException>();

        Action second = () => stream.Dispose();

        second.Should().NotThrow();
        fake.CallCounts[nameof(FaultyLibNfs.Close)].Should().Be(1);
    }
}
