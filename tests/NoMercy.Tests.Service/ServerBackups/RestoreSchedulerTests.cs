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
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class RestoreSchedulerTests : IDisposable
{
    private readonly BackupTestFolder _folder = new();
    private readonly BackupOptions _options;
    private readonly BackupService _backups;
    private readonly RestoreScheduler _scheduler;

    public RestoreSchedulerTests()
    {
        _options = _folder.Options(retainCount: 1);
        LocalStorageDriver storage = new();
        SqliteFileOperations sqlite = new();
        _backups = BackupTestFolder.CreateBackupService(_options);
        FakeBackupDatabaseSchema schema = new(
            BackupTestFolder.FirstMigration,
            BackupTestFolder.SecondMigration
        );
        _scheduler = new(
            storage,
            new BackupSetVerifier(storage, sqlite, schema, _options),
            _backups,
            _options
        );
        _folder.CreateServer();
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task AValidBackup_IsScheduled_AfterASafetyBackupOfTheCurrentState()
    {
        string id = (await _backups.CreateAsync("target", CancellationToken.None)).Id;

        RestoreScheduleResult result = await _scheduler.ScheduleAsync(id, CancellationToken.None);

        result.Status.Should().Be(RestoreScheduleStatus.Scheduled);
        PendingRestore pending = (await _scheduler.GetPendingAsync(CancellationToken.None))!;
        pending.BackupId.Should().Be(id);
        pending.SafetyBackupId.Should().NotBeNullOrEmpty().And.NotBe(id);

        IReadOnlyList<BackupSetSummary> sets = await _backups.ListAsync(CancellationToken.None);
        sets.Select(set => set.Id).Should().Contain(pending.SafetyBackupId!);
        sets.Single(set => set.Id == pending.SafetyBackupId).Reason.Should().Contain(id);
    }

    [Fact]
    public async Task TheBackupToRestore_SurvivesThePruneOfTheSafetyBackup()
    {
        // Retain count is one: without the protection, the safety backup's prune
        // would delete the very set the restore needs.
        string id = (await _backups.CreateAsync("target", CancellationToken.None)).Id;

        RestoreScheduleResult result = await _scheduler.ScheduleAsync(id, CancellationToken.None);
        await _backups.CreateAsync("a later daily backup", CancellationToken.None);

        Directory.Exists(Path.Combine(_options.SetsRoot, id)).Should().BeTrue();
        Directory
            .Exists(Path.Combine(_options.SetsRoot, result.Pending!.SafetyBackupId!))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task WhenTheSafetyBackupFails_NoRequestIsWritten()
    {
        string id = (await _backups.CreateAsync("target", CancellationToken.None)).Id;
        // A full disk while the safety set is written.
        FaultyStorageDriver full = new()
        {
            FailOpenWrite = path => path.Contains(BackupSetId.IncompletePrefix),
        };
        LocalStorageDriver storage = new();
        RestoreScheduler scheduler = new(
            storage,
            new BackupSetVerifier(
                storage,
                new SqliteFileOperations(),
                new FakeBackupDatabaseSchema(
                    BackupTestFolder.FirstMigration,
                    BackupTestFolder.SecondMigration
                ),
                _options
            ),
            BackupTestFolder.CreateBackupService(_options, storage: full),
            _options
        );

        Func<Task> schedule = () => scheduler.ScheduleAsync(id, CancellationToken.None);

        await schedule.Should().ThrowAsync<IOException>();
        File.Exists(_options.PendingRestorePath).Should().BeFalse();
    }

    [Fact]
    public async Task ASecondRequest_WhileOneIsWaiting_IsRefused()
    {
        string id = (await _backups.CreateAsync("target", CancellationToken.None)).Id;
        await _scheduler.ScheduleAsync(id, CancellationToken.None);

        RestoreScheduleResult second = await _scheduler.ScheduleAsync(id, CancellationToken.None);

        second.Status.Should().Be(RestoreScheduleStatus.AlreadyPending);
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("20240101-000000")]
    [InlineData("")]
    public async Task AnIdThatIsNotABackupName_IsRefused(string id)
    {
        RestoreScheduleResult result = await _scheduler.ScheduleAsync(id, CancellationToken.None);

        result.Status.Should().Be(RestoreScheduleStatus.InvalidId);
        File.Exists(_options.PendingRestorePath).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnknownBackup_IsNotFound()
    {
        RestoreScheduleResult result = await _scheduler.ScheduleAsync(
            "20240101-000000-0000000",
            CancellationToken.None
        );

        result.Status.Should().Be(RestoreScheduleStatus.NotFound);
    }

    [Fact]
    public async Task ADamagedBackup_IsRefusedWithItsProblems_AndNothingIsScheduled()
    {
        string id = (await _backups.CreateAsync("target", CancellationToken.None)).Id;
        File.WriteAllText(Path.Combine(_options.SetsRoot, id, "config", "settings.json"), "x");

        RestoreScheduleResult result = await _scheduler.ScheduleAsync(id, CancellationToken.None);

        result.Status.Should().Be(RestoreScheduleStatus.Invalid);
        result.Problems.Should().ContainSingle(problem => problem.Contains("settings.json"));
        File.Exists(_options.PendingRestorePath).Should().BeFalse();
        (await _backups.ListAsync(CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task Cancel_WithdrawsTheWaitingRequest()
    {
        string id = (await _backups.CreateAsync("target", CancellationToken.None)).Id;
        await _scheduler.ScheduleAsync(id, CancellationToken.None);

        bool withdrawn = await _scheduler.CancelAsync(CancellationToken.None);

        withdrawn.Should().BeTrue();
        (await _scheduler.GetPendingAsync(CancellationToken.None)).Should().BeNull();
        (await _scheduler.CancelAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task LastResult_ReadsWhatTheRestoreWrote()
    {
        Directory.CreateDirectory(_options.RestoreRoot);
        File.WriteAllText(
            _options.LastRestorePath,
            BackupFiles.Serialize(
                new RestoreResult
                {
                    BackupId = "20240101-000000-0000000",
                    Status = RestoreStatus.RolledBack,
                    Message = "undone",
                }
            )
        );

        RestoreResult? result = await _scheduler.GetLastResultAsync(CancellationToken.None);

        result!.Status.Should().Be(RestoreStatus.RolledBack);
        File.ReadAllText(_options.LastRestorePath).Should().Contain("\"RolledBack\"");
    }
}
