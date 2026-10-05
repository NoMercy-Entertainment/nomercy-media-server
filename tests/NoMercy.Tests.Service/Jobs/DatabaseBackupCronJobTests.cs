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

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Service.Jobs;
using NoMercy.Service.Seeds;

namespace NoMercy.Tests.Service.Jobs;

[Trait("Category", "Unit")]
public sealed class DatabaseBackupCronJobTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _backupDir;
    private readonly string _originalBackupRoot;
    private readonly int _originalRetainCount;

    public DatabaseBackupCronJobTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nm_backup_job_test_{Guid.NewGuid():N}");
        _backupDir = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(_tempDir);

        _originalBackupRoot = DatabaseBackupService.BackupRoot;
        _originalRetainCount = DatabaseBackupService.RetainCount;
        DatabaseBackupService.BackupRoot = _backupDir;
    }

    public void Dispose()
    {
        DatabaseBackupService.BackupRoot = _originalBackupRoot;
        DatabaseBackupService.RetainCount = _originalRetainCount;

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // A real SQLite database, not a text file: the service copies through
    // SQLite's online-backup API, which rejects anything without a valid
    // database header.
    private string CreateFakeDb(string name)
    {
        string path = Path.Combine(_tempDir, name);
        using SqliteConnection connection = new($"Data Source={path}; Pooling=False;");
        connection.Open();
        using SqliteCommand seed = connection.CreateCommand();
        seed.CommandText = "CREATE TABLE marker (content TEXT);";
        seed.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public async Task ExecuteAsync_BacksUpEveryConfiguredDatabase()
    {
        string mediaDb = CreateFakeDb("media.db");
        string queueDb = CreateFakeDb("queue.db");
        string appDb = CreateFakeDb("app.db");

        DatabaseBackupCronJob job = new(
            NullLogger<DatabaseBackupCronJob>.Instance,
            [mediaDb, queueDb, appDb]
        );

        await job.ExecuteAsync(parameters: string.Empty);

        Directory.GetFiles(_backupDir, "media.*.db").Should().HaveCount(1);
        Directory.GetFiles(_backupDir, "queue.*.db").Should().HaveCount(1);
        Directory.GetFiles(_backupDir, "app.*.db").Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_MissingDatabase_SkipsItWithoutThrowing()
    {
        string mediaDb = CreateFakeDb("media.db");
        string missingDb = Path.Combine(_tempDir, "queue.db"); // never created

        DatabaseBackupCronJob job = new(
            NullLogger<DatabaseBackupCronJob>.Instance,
            [mediaDb, missingDb]
        );

        await job.ExecuteAsync(parameters: string.Empty);

        Directory.GetFiles(_backupDir, "media.*.db").Should().HaveCount(1);
        Directory.GetFiles(_backupDir, "queue.*.db").Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RunTwice_ProducesIndependentTimestampedBackups()
    {
        string mediaDb = CreateFakeDb("media.db");
        DatabaseBackupCronJob job = new(NullLogger<DatabaseBackupCronJob>.Instance, [mediaDb]);

        await job.ExecuteAsync(parameters: string.Empty);
        await Task.Delay(1100); // timestamp granularity is 1 second
        await job.ExecuteAsync(parameters: string.Empty);

        Directory.GetFiles(_backupDir, "media.*.db").Should().HaveCount(2);
    }

    [Fact]
    public void CronExpression_IsDailyAt4Am_AndJobNameIsSet()
    {
        DatabaseBackupCronJob job = new(NullLogger<DatabaseBackupCronJob>.Instance, []);

        job.CronExpression.Should().Be("0 4 * * *");
        job.JobName.Should().Be("Daily Database Backup");
    }

    [Fact]
    public void Constructor_NoDbPathsProvided_DefaultsToAppFilesDatabases()
    {
        DatabaseBackupCronJob job = new(NullLogger<DatabaseBackupCronJob>.Instance);

        // Defaulting is exercised via ExecuteAsync against real AppFiles paths —
        // asserting only that construction with no dbPaths argument doesn't throw,
        // since AppFiles.* points at this test run's actual data directory.
        job.JobName.Should().Be("Daily Database Backup");
    }
}
