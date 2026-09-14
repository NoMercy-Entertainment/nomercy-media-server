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

using NoMercy.Setup.Server;
using NoMercy.Storage;

namespace NoMercy.Tests.Setup.Server;

[Trait("Category", "Unit")]
public class ServerUpdateStagingTests
{
    private const string Staged = "staged.exe";
    private const string Server = "server.exe";
    private const string Running = "1.5.0";

    private static (Mock<IStorageDriver> Driver, StagingCheck Check) Run(
        bool isContainer,
        bool stagedExists,
        string? stagedVersion,
        string? serverVersion
    )
    {
        Mock<IStorageDriver> driver = new();
        driver.Setup(d => d.FileExists(Staged)).Returns(stagedExists);
        StagingCheck check = ServerUpdateStaging.Check(
            driver.Object,
            isContainer,
            Running,
            Staged,
            Server,
            path => path == Staged ? stagedVersion : serverVersion
        );
        return (driver, check);
    }

    [Fact]
    public void Container_DeletesAnyStagedBinaryAndAsksForAnImage()
    {
        (Mock<IStorageDriver> driver, StagingCheck check) = Run(true, true, "9.0.0", null);

        check.State.Should().Be(StagingState.ContainerImage);
        driver.Verify(d => d.DeleteFile(Staged), Times.Once);
    }

    [Fact]
    public void NewerStagedBinary_IsKept()
    {
        (Mock<IStorageDriver> driver, StagingCheck check) = Run(false, true, "1.6.0", null);

        check.Should().Be(new StagingCheck(StagingState.AlreadyStaged, "1.6.0"));
        driver.Verify(d => d.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void StaleStagedBinary_IsDiscardedAndADownloadIsNeeded()
    {
        (Mock<IStorageDriver> driver, StagingCheck check) = Run(false, true, "1.5.0", "1.5.0");

        check
            .Should()
            .Be(new StagingCheck(StagingState.NeedsDownload, DiscardedStaleVersion: "1.5.0"));
        driver.Verify(d => d.DeleteFile(Staged), Times.Once);
    }

    [Fact]
    public void NewerBinaryOnDisk_OnlyNeedsARestart()
    {
        (_, StagingCheck check) = Run(false, false, null, "2.0.0");

        check.Should().Be(new StagingCheck(StagingState.BinaryOnDiskIsNewer, "2.0.0"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("not a version", false)]
    [InlineData("1.4.9", false)]
    [InlineData("1.5.0", false)]
    [InlineData("1.5.1", true)]
    public void IsNewer_OnlyForAParsableHigherVersion(string? candidate, bool expected)
    {
        ServerUpdateStaging.IsNewer(candidate, Running).Should().Be(expected);
    }
}
