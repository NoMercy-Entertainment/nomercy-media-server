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
            .Setup(mock =>
                mock.ApplyPendingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())
            )
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
            ServerBootstrapper.ApplyPendingRestoreAsync(applier.Object, holdBackReason: null);

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
            ServerBootstrapper.ApplyPendingRestoreAsync(applier.Object, holdBackReason: null);

        await start.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Another server process holds the data folder.")]
    public async Task TheReasonToHoldBack_IsPassedOn(string? holdBackReason)
    {
        Mock<IRestoreApplier> applier = Applier(null);

        await ServerBootstrapper.ApplyPendingRestoreAsync(applier.Object, holdBackReason);

        applier.Verify(
            mock => mock.ApplyPendingAsync(holdBackReason, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public void OnlyALockHeldByAnotherProcess_IsCalledAnotherServer()
    {
        ServerInstanceLockAttempt held = new(null, HeldElsewhere: true, "in use");
        ServerInstanceLockAttempt readOnly = new(null, HeldElsewhere: false, "Access denied");

        ServerBootstrapper
            .RestoreHoldBackReason(previousServerStopped: true, held)
            .Should()
            .Be("Another server process holds the data folder.");
        ServerBootstrapper
            .RestoreHoldBackReason(previousServerStopped: true, readOnly)
            .Should()
            .Contain("could not be locked (Access denied)");
        ServerBootstrapper
            .RestoreHoldBackReason(previousServerStopped: false, held)
            .Should()
            .Contain("had not exited");
    }
}
