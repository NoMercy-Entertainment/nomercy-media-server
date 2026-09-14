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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Storage.Drivers.Nfs;
using NoMercy.Tests.Storage.Faults;

namespace NoMercy.Tests.Storage;

/// <summary>
/// A folder whose enumeration genuinely fails (EIO, permission denied, a
/// dropped mount mid-listing) used to look identical to a folder that is
/// simply empty — CollectEntries logged a warning and returned an empty
/// list either way. A caller reconciling stale DB rows against "what this
/// pass saw" cannot tell those apart, so it treated a failed enumeration as
/// proof the folder's files are gone (issue #55, contributing cause #5).
/// Only the two expected "this path genuinely is not there" outcomes —
/// ENOENT and ENOTDIR — stay silent; everything else now propagates.
/// </summary>
[Trait("Category", "Unit")]
public class NfsCollectEntriesFaultTests
{
    private static NfsDriverConfig Config() =>
        NfsDriverConfig.For("fake-server", "/export", version: 4);

    [Fact]
    public void OpenDirFailsWithNoent_ReturnsEmptyWithoutThrowing()
    {
        FaultyLibNfs fake = new();
        fake.Faults["OpenDir:0"] = (-2, "NFS4ERR_NOENT");

        using NfsStorageDriver driver = new(Config(), fake, NullLogger.Instance);

        IEnumerable<string> entries = driver.EnumerateFileSystemEntries(
            "/missing",
            "*",
            SearchOption.TopDirectoryOnly
        );

        entries.Should().BeEmpty();
    }

    [Fact]
    public void OpenDirFailsWithNotDir_ReturnsEmptyWithoutThrowing()
    {
        FaultyLibNfs fake = new();
        fake.Faults["OpenDir:0"] = (-20, "NFS4ERR_NOTDIR");

        using NfsStorageDriver driver = new(Config(), fake, NullLogger.Instance);

        IEnumerable<string> entries = driver.EnumerateFileSystemEntries(
            "/a-file",
            "*",
            SearchOption.TopDirectoryOnly
        );

        entries.Should().BeEmpty();
    }

    [Fact]
    public void OpenDirFailsWithAnUnexpectedError_ThrowsInsteadOfReturningEmpty()
    {
        FaultyLibNfs fake = new();
        fake.Faults["OpenDir:0"] = (-5, "EIO");

        using NfsStorageDriver driver = new(Config(), fake, NullLogger.Instance);

        Action act = () =>
            driver.EnumerateFileSystemEntries("/vault", "*", SearchOption.TopDirectoryOnly);

        act.Should().Throw<IOException>().WithMessage("*opendir*");
    }
}
