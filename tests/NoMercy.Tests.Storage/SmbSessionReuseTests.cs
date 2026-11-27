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
using Moq;
using NoMercy.Storage.Drivers.Smb;
using SMBLibrary;
using SMBLibrary.Client;
using Xunit;

namespace NoMercy.Tests.Storage;

/// <summary>
/// Every SMB connect is a TCP handshake, a login and a share connect. A served
/// video segment makes several metadata calls and one read, so a connect per call
/// put seconds in front of each segment. Calls in a row share one session.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SmbSessionReuseTests
{
    private static (SmbStorageDriver Driver, Func<int> Connects) DriverCountingConnects()
    {
        Mock<ISMBFileStore> store = new();
        object handle;
        FileStatus status;
        store
            .Setup(s => s.CreateFile(
                out handle,
                out status,
                It.IsAny<string>(),
                It.IsAny<AccessMask>(),
                It.IsAny<SMBLibrary.FileAttributes>(),
                It.IsAny<ShareAccess>(),
                It.IsAny<CreateDisposition>(),
                It.IsAny<CreateOptions>(),
                It.IsAny<SecurityContext>()
            ))
            .Returns(NTStatus.STATUS_OBJECT_NAME_NOT_FOUND);
        store.Setup(s => s.Disconnect()).Returns(NTStatus.STATUS_SUCCESS);

        int connects = 0;
        SmbStorageDriver driver = new(
            new SmbDriverConfig("192.0.2.10", "Media", 445, 30),
            log: null,
            connect: () =>
            {
                Interlocked.Increment(ref connects);
                return new SmbSession { Client = new SMB2Client(), Store = store.Object };
            }
        );
        return (driver, () => connects);
    }

    [Fact]
    public void Metadata_calls_in_a_row_share_one_connection()
    {
        (SmbStorageDriver driver, Func<int> connects) = DriverCountingConnects();
        using (driver)
        {
            for (int i = 0; i < 5; i++)
                driver.FileExists($"TV.Shows/show/e{i}.m4s").Should().BeFalse();
        }

        connects().Should().Be(1, "a served segment must not log in to the NAS once per check");
    }

    [Fact]
    public void Parallel_calls_never_share_a_session_at_the_same_time()
    {
        (SmbStorageDriver driver, Func<int> connects) = DriverCountingConnects();
        using (driver)
        {
            Parallel.For(0, 32, i => driver.FileExists($"x/{i}.m4s").Should().BeFalse());
        }

        connects().Should().BeInRange(1, 32);
    }
}
