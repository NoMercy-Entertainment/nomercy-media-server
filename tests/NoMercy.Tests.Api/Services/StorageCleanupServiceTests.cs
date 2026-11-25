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

using Moq;
using NoMercy.Api.Services;
using NoMercy.Data.ServerBackups;
using NoMercy.Database;
using NoMercy.Queue.MediaServer.Jobs;
using Xunit;

namespace NoMercy.Tests.Api.Services;

[Trait("Category", "Unit")]
public sealed class StorageCleanupServiceTests
{
    private readonly Mock<IMaintenancePass<DatabaseMaintenanceResult>> _databases = new();
    private readonly Mock<IMaintenancePass<ImageCacheSweepResult?>> _images = new();
    private readonly Mock<IBackupService> _backups = new();
    private readonly MaintenanceGate _gate = new();

    private StorageCleanupService Service() =>
        new(_databases.Object, _backups.Object, _images.Object, _gate);

    private void EveryPassFinishes()
    {
        _databases
            .Setup(pass => pass.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DatabaseMaintenanceResult(
                    12,
                    new SqliteCompactionResult(true, 700, 1000, 300),
                    new SqliteCompactionResult(true, 90, 500, 410)
                )
            );
        _backups
            .Setup(service => service.PruneAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupPruneResult(2, 5000, 1, 800));
        _images
            .Setup(pass => pass.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageCacheSweepResult(3, 60, 4, 40, 9999));
    }

    [Fact]
    public async Task RunAsync_ReportsTheBytesFreedByEveryPart()
    {
        EveryPassFinishes();

        StorageCleanupResult? result = await Service().RunAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result!
            .Parts.ToDictionary(part => part.Id, part => part.BytesFreed)
            .Should()
            .Equal(
                new Dictionary<string, long>
                {
                    ["database_media"] = 700,
                    ["database_queue"] = 90,
                    ["backup_sets"] = 5000,
                    ["backup_copies"] = 800,
                    ["image_cache"] = 100,
                }
            );
        result.TotalBytesFreed.Should().Be(6690);
        result.FailedJobsRemoved.Should().Be(12);
    }

    [Fact]
    public async Task RunAsync_WhenAPassFailed_ReportsZeroForThatPartAndStillRunsTheRest()
    {
        EveryPassFinishes();
        _images
            .Setup(pass => pass.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImageCacheSweepResult?)null);

        StorageCleanupResult? result = await Service().RunAsync(CancellationToken.None);

        result!.Parts.Single(part => part.Id == "image_cache").BytesFreed.Should().Be(0);
        result.Parts.Single(part => part.Id == "backup_sets").BytesFreed.Should().Be(5000);
    }

    [Fact]
    public async Task RunAsync_WhileAnotherCleanUpRuns_DoesNotRunAgain()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EveryPassFinishes();
        _databases
            .Setup(pass => pass.RunAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                started.SetResult();
                await release.Task;
                return new DatabaseMaintenanceResult(0, null, null);
            });

        Task<StorageCleanupResult?> first = Service().RunAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        StorageCleanupResult? second = await Service().RunAsync(CancellationToken.None);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        second.Should().BeNull();
        _databases.Verify(pass => pass.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
        _backups.Verify(service => service.PruneAsync(It.IsAny<CancellationToken>()), Times.Once);
        _images.Verify(pass => pass.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhileTheNightlyPassHoldsTheGate_RunsNothing()
    {
        EveryPassFinishes();
        using IDisposable nightly = _gate.TryEnter()!;

        StorageCleanupResult? result = await Service().RunAsync(CancellationToken.None);

        result.Should().BeNull();
        _databases.Invocations.Should().BeEmpty();
        _backups.Invocations.Should().BeEmpty();
        _images.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_AfterTheFirstFinished_RunsAgain()
    {
        EveryPassFinishes();
        await Service().RunAsync(CancellationToken.None);

        StorageCleanupResult? again = await Service().RunAsync(CancellationToken.None);

        again.Should().NotBeNull();
    }
}
