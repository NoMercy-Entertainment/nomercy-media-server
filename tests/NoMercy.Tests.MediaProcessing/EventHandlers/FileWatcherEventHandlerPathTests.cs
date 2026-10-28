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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.EventHandlers;

/// <summary>
/// The watcher reports a file by its full path on disk; the scanner stores
/// the file's folder rebased onto the library folder, without the leading
/// slash. The two have to meet, or a file deleted or renamed on disk keeps
/// its row and the client lists an episode that no longer plays (#367).
/// </summary>
[Trait("Category", "Unit")]
public sealed class FileWatcherEventHandlerPathTests : IDisposable
{
    private const string LibraryRoot = "/mnt/media/TV";
    private const string SeasonOnDisk = "/mnt/media/TV/Show/Season 1";
    private const string SeasonStored = "mnt/media/TV/Show/Season 1";

    private readonly Ulid _libraryId = Ulid.NewUlid();
    private readonly Ulid _folderId = Ulid.NewUlid();
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public FileWatcherEventHandlerPathTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand foreignKeys = _connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeys.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
        context.Folders.Add(
            new()
            {
                Id = _folderId,
                Path = LibraryRoot,
                DriverId = Ulid.NewUlid(),
            }
        );
        context.FolderLibrary.Add(new(_folderId, _libraryId));
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_deleted_file_removes_its_own_row_and_no_other()
    {
        await SeedAsync(
            Row(SeasonStored, "/Show.S01E01.mkv"),
            Row(SeasonStored, "/Show.S01E02.mkv")
        );

        await Handler()
            .OnFileDeleted(
                new()
                {
                    FullPath = $"{SeasonOnDisk}/Show.S01E01.mkv",
                    LibraryId = _libraryId,
                    LibraryType = MediaTypes.TvMediaType,
                },
                CancellationToken.None
            );

        List<VideoFile> left = await RowsAsync();
        left.Should().ContainSingle().Which.Filename.Should().Be("/Show.S01E02.mkv");
    }

    /// <summary>A row stored before paths were rebased still holds the folder as it was on disk.</summary>
    [Fact]
    public async Task A_deleted_file_also_removes_a_row_stored_with_the_full_path()
    {
        await SeedAsync(Row(SeasonOnDisk, "/Show.S01E01.mkv"));

        await Handler()
            .OnFileDeleted(
                new()
                {
                    FullPath = $"{SeasonOnDisk}/Show.S01E01.mkv",
                    LibraryId = _libraryId,
                    LibraryType = MediaTypes.TvMediaType,
                },
                CancellationToken.None
            );

        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_renamed_file_updates_its_row_in_the_stored_form()
    {
        await SeedAsync(Row(SeasonStored, "/Show.S01E02.mkv"));

        await Handler()
            .OnFileRenamed(
                new()
                {
                    OldFullPath = $"{SeasonOnDisk}/Show.S01E02.mkv",
                    NewFullPath = $"{SeasonOnDisk}/Show.S01E02.Title.mkv",
                    LibraryId = _libraryId,
                    LibraryType = MediaTypes.TvMediaType,
                },
                CancellationToken.None
            );

        VideoFile row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.Filename.Should().Be("/Show.S01E02.Title.mkv");
        row.HostFolder.Should().Be(SeasonStored);
        row.Folder.Should().Be("/Show/Season 1", "Folder is measured from the library root");
    }

    /// <summary>
    /// Folder is how the file is served: /{Share}{Folder}{Filename}. Moved to
    /// another season it must name the show too, not just the last folder.
    /// </summary>
    [Fact]
    public async Task A_file_moved_to_another_season_keeps_a_folder_measured_from_the_library_root()
    {
        await SeedAsync(Row(SeasonStored, "/Show.S01E02.mkv"));

        await Handler()
            .OnFileRenamed(
                new()
                {
                    OldFullPath = $"{SeasonOnDisk}/Show.S01E02.mkv",
                    NewFullPath = "/mnt/media/TV/Show/Season 2/Show.S01E02.mkv",
                    LibraryId = _libraryId,
                    LibraryType = MediaTypes.TvMediaType,
                },
                CancellationToken.None
            );

        VideoFile row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.HostFolder.Should().Be("mnt/media/TV/Show/Season 2");
        row.Folder.Should().Be("/Show/Season 2");
    }

    private FileWatcherEventHandler Handler()
    {
        Mock<IDbContextFactory<MediaContext>> contexts = new();
        contexts
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        IStorageDriver driver = OnlineRootStorage.Driver([LibraryRoot]);

        return new(
            NullLogger<FileWatcherEventHandler>.Instance,
            new Mock<IEventBus>().Object,
            driver,
            OnlineRootStorage.Factory(driver),
            contexts.Object
        );
    }

    private VideoFile Row(string hostFolder, string filename) =>
        new()
        {
            EpisodeId = null,
            Share = _folderId.ToString(),
            Folder = "/Show/Season 1",
            HostFolder = hostFolder,
            Filename = filename,
            Quality = "1080",
            Languages = "[]",
        };

    private async Task SeedAsync(params VideoFile[] rows)
    {
        await using MediaContext context = new(_options);
        context.VideoFiles.AddRange(rows);
        await context.SaveChangesAsync();
    }

    private async Task<List<VideoFile>> RowsAsync()
    {
        await using MediaContext context = new(_options);
        return await context.VideoFiles.AsNoTracking().ToListAsync();
    }
}
