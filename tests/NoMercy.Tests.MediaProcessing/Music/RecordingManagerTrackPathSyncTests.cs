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
using NoMercy.Database.Music;
using NoMercy.MediaProcessing.Artists;
using NoMercy.MediaProcessing.Music;
using NoMercy.MediaProcessing.MusicGenres;
using NoMercy.MediaProcessing.Recordings;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// Every file the server moves on import has to take the Track rows that address it along.
/// A row left on the old name lists a track that plays nothing. These tests run the real
/// repository on SQLite and the real LocalStorage on a temp tree with made-up files.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RecordingManagerTrackPathSyncTests : IDisposable
{
    private readonly string _driveRoot;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly RecordingManager _manager;
    private readonly Folder _libraryFolder;
    private readonly string _libraryRoot;

    public RecordingManagerTrackPathSyncTests()
    {
        _driveRoot = Path.Combine(Path.GetTempPath(), $"nm-trackpath-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_driveRoot, "Music"));

        LocalStorageDriver driver = new();
        IStorage storage = new LocalStorage(driver, new StoragePathGuard([_driveRoot], driver));

        _libraryFolder = new()
        {
            Id = Ulid.NewUlid(),
            DriverId = Ulid.NewUlid(),
            Path = "Music",
        };
        _libraryRoot = storage.GetFullPath("Music").Replace('\\', '/');

        _connection = new("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using (MediaContext setup = new(_options))
            setup.Database.EnsureCreated();

        Mock<IStorageFactory> factory = new();
        factory
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storage);

        _manager = new(
            new RecordingRepository(new MediaContext(_options)),
            Mock.Of<IMusicGenreRepository>(),
            Mock.Of<IArtistRepository>(),
            driver,
            factory.Object,
            NullLogger<RecordingManager>.Instance
        );
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_driveRoot))
            Directory.Delete(_driveRoot, recursive: true);
    }

    private static MusicBrainzReleaseAppends Release() => new() { Title = "Synthetic Album" };

    private static MusicBrainzTrack Track() =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = "Synthetic Track",
            Position = 1,
        };

    private static string TargetRelativePath(
        MusicBrainzReleaseAppends release,
        MusicBrainzTrack track
    )
    {
        MusicNamingContext context = MusicEncodeDispatcher.NamingContextFor(release, track);
        return PicardNaming.Sanitize(
            $"{PicardNaming.BuildDirectory(context)}/{PicardNaming.BuildFileName(context)}.mp3"
        );
    }

    private static string WriteFile(string absolutePath, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, contents);
        return absolutePath.Replace('\\', '/');
    }

    private static MediaFile MediaFileAt(string path) =>
        new()
        {
            Path = path,
            FFprobe = new() { Format = new() { Tags = new() { ["title"] = Track().Title } } },
        };

    private async Task<Guid> SeedTrackAtAsync(string absoluteFile)
    {
        string normalized = absoluteFile.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        Track row = new()
        {
            Id = Guid.NewGuid(),
            Name = "Seeded",
            FolderId = _libraryFolder.Id,
            HostFolder = normalized[..slash],
            Filename = normalized[slash..],
            LibraryFolder = null!,
            Metadata = null!,
        };

        await using MediaContext context = new(_options);
        context.Tracks.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private async Task<string> DiskPathOfAsync(Guid trackId)
    {
        await using MediaContext context = new(_options);
        Track row = await context.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        return $"{row.HostFolder!.TrimEnd('/')}/{row.Filename!.TrimStart('/')}";
    }

    [Fact]
    public async Task A_displaced_occupant_keeps_its_track_row_pointed_at_the_file_it_was_moved_to()
    {
        MusicBrainzReleaseAppends release = Release();
        MusicBrainzTrack track = Track();
        string target = WriteFile(
            Path.Combine(_driveRoot, "Music", TargetRelativePath(release, track)),
            "occupant"
        );
        Guid occupantRow = await SeedTrackAtAsync(target);
        string source = WriteFile(Path.Combine(_driveRoot, "Incoming", "01 Track.mp3"), "incoming");

        await _manager.RelocateToPicardLayout(
            release,
            track,
            MediaFileAt(source),
            _libraryFolder,
            _libraryRoot
        );

        string occupantNow = await DiskPathOfAsync(occupantRow);
        File.Exists(occupantNow)
            .Should()
            .BeTrue("the occupant row must follow its file to the displaced name");
        File.ReadAllText(occupantNow).Should().Be("occupant");
    }

    [Fact]
    public async Task A_row_of_another_track_id_addressing_the_moved_file_follows_it_to_the_target()
    {
        MusicBrainzReleaseAppends release = Release();
        MusicBrainzTrack track = Track();
        string source = WriteFile(
            Path.Combine(_driveRoot, "Music", "Loose", "01 Track.mp3"),
            "in-library"
        );
        string unrelated = WriteFile(
            Path.Combine(_driveRoot, "Music", "Loose", "02 Other.mp3"),
            "other"
        );
        Guid staleRow = await SeedTrackAtAsync(source);
        Guid unrelatedRow = await SeedTrackAtAsync(unrelated);
        string target = $"{_libraryRoot}/{TargetRelativePath(release, track)}";

        await _manager.RelocateToPicardLayout(
            release,
            track,
            MediaFileAt(source),
            _libraryFolder,
            _libraryRoot
        );

        File.Exists(source).Should().BeFalse("the file moved");
        (await DiskPathOfAsync(staleRow)).Should().Be(target);
        (await DiskPathOfAsync(unrelatedRow)).Should().Be(unrelated);
    }
}
