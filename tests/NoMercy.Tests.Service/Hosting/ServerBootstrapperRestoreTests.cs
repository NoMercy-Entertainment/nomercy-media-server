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

using NoMercy.Data.ServerBackups;
using NoMercy.Service.Hosting;

namespace NoMercy.Tests.Service.Hosting;

[Trait("Category", "Unit")]
public sealed class ServerBootstrapperRestoreTests
{
    private static Mock<IRestoreApplier> Applier(RestoreResult? result)
    {
        Mock<IRestoreApplier> applier = new();
        applier
            .Setup(mock => mock.ApplyPendingAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return applier;
    }

    [Fact]
    public async Task ARestoreThatCouldNotBeUndone_StopsTheStart()
    {
        Mock<IRestoreApplier> applier = Applier(
            new RestoreResult
            {
                BackupId = "20261116-040000-0000000",
                Status = RestoreStatus.RollbackIncomplete,
                Message = "media.db could not be put back.",
            }
        );

        Func<Task> start = () =>
            ServerBootstrapper.ApplyPendingRestoreAsync(applier.Object, noOtherServerRunning: true);

        (await start.Should().ThrowAsync<StartupAbortException>())
            .Which.Message.Should()
            .Contain("media.db could not be put back.");
    }

    [Theory]
    [InlineData(RestoreStatus.Applied)]
    [InlineData(RestoreStatus.Refused)]
    [InlineData(RestoreStatus.RolledBack)]
    public async Task AWholeOutcome_LetsTheStartGoOn(RestoreStatus status)
    {
        Mock<IRestoreApplier> applier = Applier(
            new RestoreResult { BackupId = "20261116-040000-0000000", Status = status }
        );

        Func<Task> start = () =>
            ServerBootstrapper.ApplyPendingRestoreAsync(applier.Object, noOtherServerRunning: true);

        await start.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhetherAnotherServerRuns_IsPassedOn(bool noOtherServerRunning)
    {
        Mock<IRestoreApplier> applier = Applier(null);

        await ServerBootstrapper.ApplyPendingRestoreAsync(applier.Object, noOtherServerRunning);

        applier.Verify(
            mock => mock.ApplyPendingAsync(noOtherServerRunning, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }
}
