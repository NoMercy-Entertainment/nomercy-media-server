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

using System.Globalization;
using NoMercy.Data.ServerBackups;

namespace NoMercy.Tests.Service.ServerBackups;

/// <summary>
/// What the backups folder holds for nothing is given back: the copies of one
/// database that every kept set is newer than, and the folder a failed backup
/// left half built. A set that cannot fit is not started.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BackupServiceRoomTests : IDisposable
{
    private const string Old = "20200101040000";
    private const string FarAhead = "29990101040000";

    private static readonly string[] Databases = ["app", "media", "queue"];

    private readonly BackupTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>
    /// The server this was found on: five daily copies of every database from
    /// before the daily backup became a set, and nothing that ever removed them.
    /// </summary>
    [Fact]
    public async Task Old_copies_of_every_database_go_with_the_daily_run()
    {
        _folder.CreateServer();
        BackupService service = Service();
        await service.CreateAsync("first set", CancellationToken.None);
        foreach (string database in Databases)
        foreach (int day in Enumerable.Range(12, 5))
            WriteCopy($"{database}.202011{day}040000.db");

        await service.CreateAsync("daily scheduled backup", CancellationToken.None);

        CopiesLeft().Should().BeEmpty();
        Directory.GetDirectories(Sets()).Should().HaveCount(2);
    }

    [Fact]
    public async Task The_first_set_ever_made_takes_the_older_copies_away()
    {
        _folder.CreateServer();
        WriteCopy($"media.{Old}.db");

        await Service().CreateAsync("first set", CancellationToken.None);

        CopiesLeft().Should().BeEmpty();
    }

    /// <summary>
    /// A copy made before an update can be the last state from before it. It
    /// stays for as long as a kept set is older than it.
    /// </summary>
    [Fact]
    public async Task A_copy_made_since_the_oldest_kept_set_stays()
    {
        _folder.CreateServer();
        BackupService service = Service();
        await service.CreateAsync("older set", CancellationToken.None);
        string sinceThen = $"media.{Now()}.db";
        WriteCopy(sinceThen);
        WriteCopy($"media.{FarAhead}.db");

        await service.CreateAsync("newer set", CancellationToken.None);

        CopiesLeft().Should().BeEquivalentTo(sinceThen, $"media.{FarAhead}.db");
    }

    /// <summary>Once the set that was older than the copy is gone, the copy is older than every kept set.</summary>
    [Fact]
    public async Task A_copy_goes_when_every_kept_set_is_newer()
    {
        _folder.CreateServer();
        BackupService service = Service(retainCount: 1);
        await service.CreateAsync("older set", CancellationToken.None);
        WriteCopy($"media.{Now()}.db");

        // A copy's name is to the second; the next set has to fall in a later one.
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        await service.CreateAsync("newer set", CancellationToken.None);

        Directory.GetDirectories(Sets()).Should().ContainSingle();
        CopiesLeft().Should().BeEmpty();
    }

    [Fact]
    public async Task The_files_sqlite_kept_beside_an_old_copy_go_with_it()
    {
        _folder.CreateServer();
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            WriteCopy($"queue.{Old}.db{suffix}");

        await Service().CreateAsync("daily scheduled backup", CancellationToken.None);

        CopiesLeft().Should().BeEmpty();
    }

    /// <summary>Only what the server itself wrote there is its to remove.</summary>
    [Fact]
    public async Task Files_that_are_not_copies_of_this_servers_databases_stay()
    {
        _folder.CreateServer();
        string[] notOurs =
        [
            "notes.txt",
            $"other.{Old}.db",
            "media.db",
            "media.2020.db",
            $"media.{Old}.db.bak",
            $"media.{Old}.sqlite",
        ];
        foreach (string file in notOurs)
            WriteCopy(file);

        await Service().CreateAsync("daily scheduled backup", CancellationToken.None);

        CopiesLeft().Should().BeEquivalentTo(notOurs);
    }

    /// <summary>
    /// A write that fails on a full disk must not leave its half-built folder
    /// or the old copies behind: they are what filled the disk.
    /// </summary>
    [Fact]
    public async Task Room_is_made_before_the_write_so_a_failed_write_leaves_nothing_behind()
    {
        _folder.CreateServer();
        await Service().CreateAsync("first set", CancellationToken.None);
        WriteCopy($"media.{Old}.db");
        string halfBuilt = Path.Combine(Sets(), ".incomplete-20261121-040018-5076004");
        Directory.CreateDirectory(Path.Combine(halfBuilt, "databases"));
        File.WriteAllText(Path.Combine(halfBuilt, "databases", "media.db"), "half");

        BackupService failing = BackupTestFolder.CreateBackupService(
            _folder.Options(),
            writer: new FailingWriter()
        );
        Func<Task> run = () =>
            failing.CreateAsync("daily scheduled backup", CancellationToken.None);

        await run.Should().ThrowAsync<IOException>().WithMessage("No space left on device*");
        Directory.Exists(halfBuilt).Should().BeFalse();
        CopiesLeft().Should().BeEmpty();
        Directory.GetDirectories(Sets()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_set_that_does_not_fit_is_not_started_and_the_reason_names_both_sizes()
    {
        _folder.CreateServer();
        FailingWriter writer = new();
        BackupService service = BackupTestFolder.CreateBackupService(
            _folder.Options(),
            writer: writer,
            volumes: new Volumes(free: 10)
        );

        Func<Task> run = () =>
            service.CreateAsync("daily scheduled backup", CancellationToken.None);

        (await run.Should().ThrowAsync<IOException>())
            .Which.Message.Should()
            .StartWith("Not enough free disk space for a backup")
            .And.Contain("10 bytes are free")
            .And.Contain(_folder.BackupRoot);
        writer.Calls.Should().Be(0);
        Directory.Exists(Sets()).Should().BeFalse();
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(null)]
    public async Task With_room_or_without_an_answer_about_room_the_set_is_made(long? free)
    {
        _folder.CreateServer();
        BackupService service = BackupTestFolder.CreateBackupService(
            _folder.Options(),
            volumes: new Volumes(free)
        );

        BackupSetSummary backup = await service.CreateAsync("manual", CancellationToken.None);

        Directory.Exists(Path.Combine(Sets(), backup.Id)).Should().BeTrue();
    }

    private BackupService Service(int retainCount = BackupOptions.DefaultRetainCount) =>
        BackupTestFolder.CreateBackupService(_folder.Options(retainCount));

    private string Sets() => Path.Combine(_folder.BackupRoot, "sets");

    private static string Now() =>
        DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private void WriteCopy(string fileName)
    {
        Directory.CreateDirectory(_folder.BackupRoot);
        File.WriteAllText(Path.Combine(_folder.BackupRoot, fileName), "a copy");
    }

    /// <summary>The files at the top of the backups folder, by name.</summary>
    private List<string> CopiesLeft() =>
        Directory.GetFiles(_folder.BackupRoot).Select(path => Path.GetFileName(path)).ToList();

    private sealed class Volumes(long? free) : IVolumeInspector
    {
        public string? VolumeOf(string path) => null;

        public bool IsMountPoint(string path) => false;

        public long? FreeBytesAt(string path) => free;
    }

    private sealed class FailingWriter : IBackupSetWriter
    {
        public int Calls { get; private set; }

        public Task<BackupManifest> WriteAsync(
            string reason,
            IReadOnlyList<BackupDatabaseFile> databases,
            string configRoot,
            bool keepDamagedCopies,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            throw new IOException("No space left on device : 'media.db'");
        }
    }
}
