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

namespace NoMercy.Tests.Storage.Faults;

/// <summary>
/// Pins the NFS4ERR_EXPIRED / BAD_SESSION / BAD_STATEID / STALE_CLIENTID
/// recovery contract: when libnfs reports any of these on the first attempt,
/// the driver tears down its context, remounts, and retries the operation
/// once. A second failure surfaces the error to the caller.
///
/// Every test runs the driver against <see cref="FaultyLibNfs"/> — no Docker,
/// no NFS server, no flake.
/// </summary>
[Trait("Category", "Unit")]
public class NfsExpiredRecoveryTests
{
    private static NfsDriverConfig BuildConfig() =>
        new(
            Server: "test.local",
            Export: "/export",
            Version: 4,
            Uid: null,
            Gid: null,
            Port: 2049,
            MountPort: null
        );

    private static (NfsStorageDriver driver, FaultyLibNfs lib) BuildDriver()
    {
        FaultyLibNfs lib = new();
        lib.SeedDir("/");
        NfsStorageDriver driver = new(BuildConfig(), lib);
        return (driver, lib);
    }

    [Fact]
    public void OpenWrite_exclusive_failure_never_truncates_with_Creat()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            lib.Faults["Open:0"] = (-17, "EEXIST");

            Action act = () => driver.OpenWrite("/new.bin", overwrite: false);

            act.Should().Throw<IOException>();
            lib.CallCounts.GetValueOrDefault("Creat").Should().Be(0);
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Theory]
    [InlineData("/tvshows/a", "/tvshows/a")]
    [InlineData("/tv/a", "/a")]
    [InlineData("/tv", "/")]
    public void Export_prefix_is_stripped_only_at_a_path_boundary(string path, string expected)
    {
        FaultyLibNfs lib = new();
        lib.SeedDir("/");
        NfsStorageDriver driver = new(BuildConfig() with { Export = "/tv" }, lib);
        try
        {
            lib.Seed(expected, [1]);

            driver.FileExists(path).Should().BeTrue();
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void Failed_remount_keeps_a_valid_context_for_the_next_operation()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            lib.Seed("/file.bin", [1]);
            lib.Faults["Stat64:0"] = (-11, "NFS4ERR_EXPIRED");
            lib.Faults["InitContext:1"] = (-1, "init failed");

            driver.FileExists("/file.bin");

            driver.FileExists("/file.bin").Should().BeTrue();
            lib.CallCounts.GetValueOrDefault("DestroyContext").Should().Be(0);
            lib.ZeroContextStatCalls.Should().Be(0);
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void OpenRead_recovers_when_first_Open_returns_NFS4ERR_EXPIRED()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            byte[] payload = [1, 2, 3, 4, 5];
            lib.Seed("/file.bin", payload);

            // First Open returns EXPIRED, second succeeds via remount path.
            // Stat is fine throughout (the first Stat in OpenRead uses lib's
            // call counter, then Open's first call hits the fault).
            lib.Faults["Open:0"] = (-11, "NFS4ERR_EXPIRED(-11)");

            using Stream s = driver.OpenRead("/file.bin");
            byte[] buffer = new byte[payload.Length];
            int read = s.Read(buffer, 0, buffer.Length);

            read.Should().Be(payload.Length);
            buffer.Should().BeEquivalentTo(payload);

            lib.CallCounts["Open"].Should().Be(2, "first Open=EXPIRED triggers Remount + retry");
            lib.CallCounts["Mount"]
                .Should()
                .BeGreaterThan(1, "Remount calls Mount on the fresh ctx");
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void OpenRead_recovers_when_first_Stat_returns_NFS4ERR_BAD_SESSION()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            lib.Seed("/file.bin", [42]);
            lib.Faults["Stat64:0"] = (-1, "NFS4ERR_BAD_SESSION");

            using Stream s = driver.OpenRead("/file.bin");
            s.Length.Should().Be(1);

            lib.CallCounts["Stat64"]
                .Should()
                .Be(2, "first Stat=BAD_SESSION triggers Remount + retry");
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void OpenRead_recovers_when_BAD_STATEID()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            lib.Seed("/file.bin", [1]);
            lib.Faults["Open:0"] = (-1, "NFS4ERR_BAD_STATEID");

            using Stream s = driver.OpenRead("/file.bin");
            s.Length.Should().Be(1);

            lib.CallCounts["Open"].Should().Be(2);
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void OpenRead_recovers_when_STALE_CLIENTID()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            lib.Seed("/file.bin", [1]);
            lib.Faults["Open:0"] = (-1, "NFS4ERR_STALE_CLIENTID");

            using Stream s = driver.OpenRead("/file.bin");
            s.Length.Should().Be(1);

            lib.CallCounts["Open"].Should().Be(2);
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void OpenRead_does_not_remount_for_NOENT()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            // Stat returns NOENT (-2) — that's the "not found" outcome,
            // not a state-expiry. Driver must NOT remount; just throw.
            Action act = () => driver.OpenRead("/missing.bin");
            act.Should().Throw<FileNotFoundException>();

            lib.CallCounts["Stat64"].Should().Be(1, "no remount on plain NOENT");
            // Mount is called once during ctor; no extra Mount from Remount.
            lib.CallCounts["Mount"].Should().Be(1);
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void OpenRead_throws_when_remount_also_fails_with_EXPIRED()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            lib.Seed("/file.bin", [1]);
            // Both attempts fail. Driver retries exactly once, then surfaces.
            lib.Faults["Open:0"] = (-11, "NFS4ERR_EXPIRED");
            lib.Faults["Open:1"] = (-1, "NFS4ERR_PERM");

            Action act = () => driver.OpenRead("/file.bin");
            act.Should().Throw<IOException>();

            lib.CallCounts["Open"].Should().Be(2, "exactly one remount-retry, no infinite loop");
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void OpenWrite_recovers_when_first_Open_returns_EXPIRED()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            lib.Faults["Open:0"] = (-11, "NFS4ERR_EXPIRED");

            using Stream s = driver.OpenWrite("/new.bin", overwrite: true);
            byte[] payload = [1, 2, 3];
            s.Write(payload, 0, payload.Length);
            s.Flush();

            lib.CallCounts["Open"].Should().BeGreaterThanOrEqualTo(2, "Remount + retry");
            lib.Files.Should().ContainKey("/new.bin");
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void Remount_runs_full_init_version_uid_mount_sequence()
    {
        FaultyLibNfs lib = new();
        lib.SeedDir("/");
        NfsDriverConfig config = new(
            Server: "test.local",
            Export: "/export",
            Version: 4,
            Uid: 1000,
            Gid: 1000,
            Port: 2049,
            MountPort: null
        );
        NfsStorageDriver driver = new(config, lib);
        try
        {
            lib.Seed("/file.bin", [1]);
            lib.Faults["Open:0"] = (-11, "NFS4ERR_EXPIRED");

            using Stream _ = driver.OpenRead("/file.bin");

            // Constructor: 1 InitContext + 1 SetVersion + 1 SetUid + 1 SetGid + 1 Mount
            // Remount: +1 of each; the old context is destroyed after the new mount.
            lib.CallCounts["InitContext"].Should().Be(2);
            lib.CallCounts["SetVersion"].Should().Be(2);
            lib.CallCounts["SetUid"].Should().Be(2);
            lib.CallCounts["SetGid"].Should().Be(2);
            lib.CallCounts["Mount"].Should().Be(2);
            lib.CallCounts["DestroyContext"].Should().Be(1, "old ctx destroyed after remount");
        }
        finally
        {
            driver.Dispose();
        }
    }

    [Fact]
    public void OpenRead_only_retries_once_even_on_repeated_EXPIRED()
    {
        (NfsStorageDriver driver, FaultyLibNfs lib) = BuildDriver();
        try
        {
            lib.Seed("/file.bin", [1]);
            // Make every Stat attempt return EXPIRED to prove we don't loop forever.
            lib.Faults["Stat64:0"] = (-11, "NFS4ERR_EXPIRED");
            lib.Faults["Stat64:1"] = (-11, "NFS4ERR_EXPIRED");
            lib.Faults["Stat64:2"] = (-11, "NFS4ERR_EXPIRED");

            Action act = () => driver.OpenRead("/file.bin");
            act.Should().Throw<Exception>();

            lib.CallCounts["Stat64"].Should().Be(2, "exactly one retry attempt, no loop");
        }
        finally
        {
            driver.Dispose();
        }
    }
}
