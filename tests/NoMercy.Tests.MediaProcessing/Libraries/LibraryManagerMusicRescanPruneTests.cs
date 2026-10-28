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
using NoMercy.Database.Models.Music;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using NoMercy.Tests.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Libraries;

/// <summary>
/// A music rescan must drop the Track rows whose file is gone: they list a track that plays
/// nothing. A row the user holds in a playlist or likes is kept, and nothing is dropped from
/// a library root that looks unmounted. Real LocalStorage on a temp tree with made-up files.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LibraryManagerMusicRescanPruneTests : IDisposable
{
    private readonly string _driveRoot;
    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;
    private readonly IStorage _storage;
    private readonly Folder _folder;
    private readonly Library _library;

    public LibraryManagerMusicRescanPruneTests()
    {
        _driveRoot = Path.Combine(Path.GetTempPath(), $"nm-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_driveRoot, "Music"));

        LocalStorageDriver driver = new();
        _storage = new LocalStorage(driver, new StoragePathGuard([_driveRoot], driver));

        _connection = new("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _context = new(new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _folder = new()
        {
            Id = Ulid.NewUlid(),
            DriverId = Ulid.NewUlid(),
            Path = "Music",
        };
        _library = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Test Music",
            Type = MediaTypes.MusicMediaType,
        };
        _context.Folders.Add(_folder);
        _context.Libraries.Add(_library);
        _context.FolderLibrary.Add(new(_folder.Id, _library.Id));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_driveRoot))
            Directory.Delete(_driveRoot, recursive: true);
    }

    private string AlbumFolder => Path.Combine(_driveRoot, "Music", "Synthetic Artist", "Album");

    private string WriteAlbumFile(string name)
    {
        Directory.CreateDirectory(AlbumFolder);
        string path = Path.Combine(AlbumFolder, name);
        File.WriteAllText(path, "audio");
        return path.Replace('\\', '/');
    }

    private Guid SeedTrackAt(string absoluteFile)
    {
        string normalized = absoluteFile.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        Track row = new()
        {
            Id = Guid.NewGuid(),
            Name = Path.GetFileNameWithoutExtension(normalized),
            FolderId = _folder.Id,
            HostFolder = normalized[..slash],
            Filename = normalized[slash..],
            LibraryFolder = null!,
            Metadata = null!,
        };
        _context.Tracks.Add(row);
        _context.SaveChanges();
        return row.Id;
    }

    // newItemsOnly is the path LibraryScanJob takes: the dashboard rescan.
    private async Task RescanAsync(bool newItemsOnly)
    {
        Mock<IStorageFactory> factory = new();
        factory
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(_storage);
        Mock<JobDispatcher> dispatcher = new();

        LibraryManager manager = new(
            new LibraryRepository(_context, new LocalStorageDriver()),
            dispatcher.Object,
            _context,
            new LocalStorageDriver(),
            factory.Object,
            new Mock<IMediaAnalyzer>().Object,
            TestFilenameParser.Default,
            NullLogger<LibraryManager>.Instance
        );

        if (newItemsOnly)
            await manager.ProcessNewLibraryItems(_library.Id);
        else
            await manager.ProcessLibrary(_library.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_rescan_marks_the_track_row_whose_file_is_gone_and_leaves_the_live_one(
        bool newItemsOnly
    )
    {
        Guid live = SeedTrackAt(WriteAlbumFile("01 Live.mp3"));
        Guid stale = SeedTrackAt(Path.Combine(AlbumFolder, "05 Old Name.mp3"));

        await RescanAsync(newItemsOnly);

        List<Track> rows = await _context.Tracks.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(t => t.Id == stale).MissingSince.Should().NotBeNull();
        rows.Single(t => t.Id == live).MissingSince.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_rescan_removes_a_track_row_that_has_been_missing_for_thirty_days(
        bool newItemsOnly
    )
    {
        Guid live = SeedTrackAt(WriteAlbumFile("01 Live.mp3"));
        Guid expired = SeedTrackAt(Path.Combine(AlbumFolder, "05 Old Name.mp3"));
        await _context
            .Tracks.Where(t => t.Id == expired)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.MissingSince, DateTime.UtcNow.AddDays(-31)));

        await RescanAsync(newItemsOnly);

        (await _context.Tracks.AsNoTracking().Select(t => t.Id).ToListAsync())
            .Should()
            .BeEquivalentTo([live]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_rescan_keeps_a_missing_track_a_user_has_in_a_playlist_or_likes(
        bool newItemsOnly
    )
    {
        WriteAlbumFile("01 Live.mp3");
        Guid inPlaylist = SeedTrackAt(Path.Combine(AlbumFolder, "02 Gone.mp3"));
        Guid liked = SeedTrackAt(Path.Combine(AlbumFolder, "03 Gone.mp3"));
        _context.PlaylistTrack.Add(new(Guid.NewGuid(), inPlaylist));
        _context.TrackUser.Add(new(liked, Guid.NewGuid()));
        await _context.SaveChangesAsync();

        await RescanAsync(newItemsOnly);

        List<Guid> remaining = await _context.Tracks.AsNoTracking().Select(t => t.Id).ToListAsync();
        remaining.Should().BeEquivalentTo([inPlaylist, liked]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_rescan_of_a_library_root_with_nothing_in_it_removes_nothing(
        bool newItemsOnly
    )
    {
        Guid row = SeedTrackAt(Path.Combine(AlbumFolder, "01 Unmounted.mp3"));

        await RescanAsync(newItemsOnly);

        (await _context.Tracks.AsNoTracking().Select(t => t.Id).ToListAsync())
            .Should()
            .BeEquivalentTo([row]);
    }
}
