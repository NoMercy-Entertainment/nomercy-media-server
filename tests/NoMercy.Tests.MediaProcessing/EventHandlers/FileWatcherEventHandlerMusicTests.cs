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
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.EventHandlers;

/// <summary>
/// A music file deleted or renamed on disk reaches its Track rows: a delete marks the row
/// unavailable, a rename repoints it to the new path.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FileWatcherEventHandlerMusicTests : IDisposable
{
    private const string LibraryRoot = "/mnt/media/Music";
    private const string AlbumOnDisk = "/mnt/media/Music/Synthetic Artist/Album";

    private readonly Ulid _libraryId = Ulid.NewUlid();
    private readonly Ulid _folderId = Ulid.NewUlid();
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public FileWatcherEventHandlerMusicTests()
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
    public async Task A_deleted_music_file_marks_its_row_unavailable_and_no_other()
    {
        Guid gone = await SeedAsync(AlbumOnDisk, "/01 Gone.flac");
        Guid kept = await SeedAsync(AlbumOnDisk, "/02 Kept.flac");

        await Handler()
            .OnFileDeleted(
                new()
                {
                    FullPath = $"{AlbumOnDisk}/01 Gone.flac",
                    LibraryId = _libraryId,
                    LibraryType = MediaTypes.MusicMediaType,
                },
                CancellationToken.None
            );

        List<Track> rows = await RowsAsync();
        rows.Single(row => row.Id == gone).MissingSince.Should().NotBeNull();
        rows.Single(row => row.Id == kept).MissingSince.Should().BeNull();
    }

    [Fact]
    public async Task A_renamed_music_file_repoints_its_row_and_clears_the_mark()
    {
        Guid id = await SeedAsync(AlbumOnDisk, "/01 Old.flac", missingSince: DateTime.UtcNow);

        await Handler()
            .OnFileRenamed(
                new()
                {
                    OldFullPath = $"{AlbumOnDisk}/01 Old.flac",
                    NewFullPath = $"{LibraryRoot}/Synthetic Artist/Album 2/01 New.flac",
                    LibraryId = _libraryId,
                    LibraryType = MediaTypes.MusicMediaType,
                },
                CancellationToken.None
            );

        Track row = (await RowsAsync()).Single(track => track.Id == id);
        row.Filename.Should().Be("/01 New.flac");
        row.HostFolder.Should().Be($"{LibraryRoot}/Synthetic Artist/Album 2");
        row.Folder.Should().Be("/Synthetic Artist/Album 2");
        row.MissingSince.Should().BeNull();
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

    private async Task<Guid> SeedAsync(
        string hostFolder,
        string filename,
        DateTime? missingSince = null
    )
    {
        Track row = new()
        {
            Id = Guid.NewGuid(),
            Name = filename,
            FolderId = _folderId,
            Folder = "/Synthetic Artist/Album",
            HostFolder = hostFolder,
            Filename = filename,
            MissingSince = missingSince,
            LibraryFolder = null!,
            Metadata = null!,
        };
        await using MediaContext context = new(_options);
        context.Tracks.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private async Task<List<Track>> RowsAsync()
    {
        await using MediaContext context = new(_options);
        return await context.Tracks.AsNoTracking().ToListAsync();
    }
}
