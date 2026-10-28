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
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.EventHandlers;

/// <summary>
/// A share that goes offline makes every file look deleted. The handler must not turn
/// that into deleted rows: an unreadable root, an empty root and a burst of deletes
/// leave the rows alone.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FileWatcherEventHandlerOfflineRootTests : IDisposable
{
    private const string LibraryRoot = "/mnt/media/TV";
    private const string SeasonOnDisk = "/mnt/media/TV/Show/Season 1";
    private const string SeasonStored = "mnt/media/TV/Show/Season 1";
    private const string MusicRoot = "/mnt/media/Music";
    private const string AlbumOnDisk = "/mnt/media/Music/Synthetic Artist/Album";

    private readonly Ulid _libraryId = Ulid.NewUlid();
    private readonly Ulid _folderId = Ulid.NewUlid();
    private readonly Ulid _musicLibraryId = Ulid.NewUlid();
    private readonly Ulid _musicFolderId = Ulid.NewUlid();
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public FileWatcherEventHandlerOfflineRootTests()
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
        context.Folders.Add(
            new()
            {
                Id = _musicFolderId,
                Path = MusicRoot,
                DriverId = Ulid.NewUlid(),
            }
        );
        context.FolderLibrary.Add(new(_musicFolderId, _musicLibraryId));
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task An_unreadable_root_deletes_no_video_row()
    {
        await SeedVideoAsync("/Show.S01E01.mkv", "/Show.S01E02.mkv");

        await DeleteVideoAsync(
            Handler(rootReadable: false, rootHasEntries: true),
            "/Show.S01E01.mkv"
        );

        (await VideoRowsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_empty_root_deletes_no_video_row()
    {
        await SeedVideoAsync("/Show.S01E01.mkv", "/Show.S01E02.mkv");

        await DeleteVideoAsync(
            Handler(rootReadable: true, rootHasEntries: false),
            "/Show.S01E01.mkv"
        );

        (await VideoRowsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_burst_of_deletes_stops_before_it_empties_the_library()
    {
        string[] names = [.. Enumerable.Range(1, 20).Select(n => $"/Show.S01E{n:00}.mkv")];
        await SeedVideoAsync(names);
        FileWatcherEventHandler handler = Handler(rootReadable: true, rootHasEntries: true);

        foreach (string name in names)
            await DeleteVideoAsync(handler, name);

        (await VideoRowsAsync()).Count.Should().BeGreaterThan(10);
    }

    [Fact]
    public async Task A_single_delete_on_a_readable_root_still_removes_its_row()
    {
        await SeedVideoAsync("/Show.S01E01.mkv", "/Show.S01E02.mkv");

        await DeleteVideoAsync(
            Handler(rootReadable: true, rootHasEntries: true),
            "/Show.S01E01.mkv"
        );

        (await VideoRowsAsync())
            .Should()
            .ContainSingle()
            .Which.Filename.Should()
            .Be("/Show.S01E02.mkv");
    }

    [Fact]
    public async Task An_unreadable_root_marks_no_music_row()
    {
        Guid id = await SeedTrackAsync("/01 Song.flac");

        await Handler(rootReadable: false, rootHasEntries: true)
            .OnFileDeleted(
                new()
                {
                    FullPath = $"{AlbumOnDisk}/01 Song.flac",
                    LibraryId = _musicLibraryId,
                    LibraryType = MediaTypes.MusicMediaType,
                },
                CancellationToken.None
            );

        await using MediaContext context = new(_options);
        (await context.Tracks.AsNoTracking().SingleAsync(track => track.Id == id))
            .MissingSince.Should()
            .BeNull();
    }

    private Task DeleteVideoAsync(FileWatcherEventHandler handler, string filename) =>
        handler.OnFileDeleted(
            new()
            {
                FullPath = SeasonOnDisk + filename,
                LibraryId = _libraryId,
                LibraryType = MediaTypes.TvMediaType,
            },
            CancellationToken.None
        );

    private FileWatcherEventHandler Handler(bool rootReadable, bool rootHasEntries)
    {
        Mock<IDbContextFactory<MediaContext>> contexts = new();
        contexts
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        IStorageDriver driver = OnlineRootStorage.Driver(
            [LibraryRoot, MusicRoot],
            rootReadable,
            rootHasEntries
        );

        return new(
            NullLogger<FileWatcherEventHandler>.Instance,
            new Mock<IEventBus>().Object,
            driver,
            OnlineRootStorage.Factory(driver),
            contexts.Object
        );
    }

    private async Task SeedVideoAsync(params string[] filenames)
    {
        await using MediaContext context = new(_options);
        context.VideoFiles.AddRange(
            filenames.Select(filename => new VideoFile
            {
                EpisodeId = null,
                Share = _folderId.ToString(),
                Folder = "/Show/Season 1",
                HostFolder = SeasonStored,
                Filename = filename,
                Quality = "1080",
                Languages = "[]",
            })
        );
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedTrackAsync(string filename)
    {
        Track row = new()
        {
            Id = Guid.NewGuid(),
            Name = filename,
            FolderId = _musicFolderId,
            Folder = "/Synthetic Artist/Album",
            HostFolder = AlbumOnDisk,
            Filename = filename,
            LibraryFolder = null!,
            Metadata = null!,
        };
        await using MediaContext context = new(_options);
        context.Tracks.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private async Task<List<VideoFile>> VideoRowsAsync()
    {
        await using MediaContext context = new(_options);
        return await context.VideoFiles.AsNoTracking().ToListAsync();
    }
}
