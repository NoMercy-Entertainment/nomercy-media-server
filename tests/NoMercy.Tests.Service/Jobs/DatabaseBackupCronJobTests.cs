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
using NoMercy.Data.ServerBackups;
using NoMercy.Service.Jobs;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Service.ServerBackups;

namespace NoMercy.Tests.Service.Jobs;

[Trait("Category", "Unit")]
public sealed class DatabaseBackupCronJobTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task ExecuteAsync_WritesACompleteBackupSet()
    {
        _folder.CreateServer();
        BackupService backups = new(
            new LocalStorageDriver(),
            new SqliteFileOperations(),
            _folder.Options(),
            TimeProvider.System,
            NullLogger<BackupService>.Instance
        );
        DatabaseBackupCronJob job = new(NullLogger<DatabaseBackupCronJob>.Instance, backups);

        await job.ExecuteAsync(string.Empty);

        BackupSetSummary backup = (await backups.ListAsync(CancellationToken.None))
            .Should()
            .ContainSingle()
            .Subject;
        backup.Reason.Should().Be(DatabaseBackupCronJob.Reason);
        backup.Databases.Should().HaveCount(3);
        backup.ConfigFileCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheBackupFails_DoesNotThrow()
    {
        Mock<IBackupService> backups = new();
        backups
            .Setup(service =>
                service.CreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new IOException("disk full"));
        DatabaseBackupCronJob job = new(NullLogger<DatabaseBackupCronJob>.Instance, backups.Object);

        Func<Task> execute = () => job.ExecuteAsync(string.Empty);

        await execute.Should().NotThrowAsync();
    }

    [Fact]
    public void CronExpression_IsDailyAtFour()
    {
        DatabaseBackupCronJob job = new(
            NullLogger<DatabaseBackupCronJob>.Instance,
            Mock.Of<IBackupService>()
        );

        job.CronExpression.Should().Be("0 4 * * *");
    }
}
