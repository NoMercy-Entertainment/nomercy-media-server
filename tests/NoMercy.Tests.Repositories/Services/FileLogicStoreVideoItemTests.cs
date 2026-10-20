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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Factory;

namespace NoMercy.Tests.Repositories.Services;

// FileLogic writes the row for a scanned file with one upsert, and SQLite only
// accepts a conflict target that names a unique index. VideoFiles has exactly
// one, (Filename, HostFolder). An upsert that names Filename alone is refused,
// and when that refusal was caught and logged the scan ended with no row for a
// file it had found. The scan runs here as FindMediaFilesJob runs it: a folder
// on disk behind the real local driver and a real SQLite MediaContext, so on
// Windows the driver's backslash paths meet the stored forward-slash folder.
[Trait("Category", "Unit")]
public sealed class FileLogicStoreVideoItemTests : IDisposable
{
    private const int MovieId = 4242;
    private const string MovieFolderName = "Paper Lanterns (2019)";
    private const string FileName = "Paper.Lanterns.2019.1080p.mkv";

    private static readonly Ulid DriverId = Ulid.NewUlid();
    private static readonly Ulid FolderId = Ulid.NewUlid();
    private static readonly Ulid LibraryId = Ulid.NewUlid();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nm_file_logic_" + Guid.NewGuid().ToString("N")
    );
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly IStorageFactory _storageFactory;

    public FileLogicStoreVideoItemTests()
    {
        string libraryRoot = Path.Combine(_root, "Movies");
        Directory.CreateDirectory(Path.Combine(libraryRoot, MovieFolderName));
        File.WriteAllBytes(Path.Combine(libraryRoot, MovieFolderName, FileName), []);

        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();
        ctx.Drivers.Add(
            new()
            {
                Id = DriverId,
                Name = "Local Filesystem",
                Type = "local",
            }
        );
        ctx.Folders.Add(
            new()
            {
                Id = FolderId,
                DriverId = DriverId,
                Path = libraryRoot,
            }
        );
        ctx.Libraries.Add(
            new()
            {
                Id = LibraryId,
                Title = "Movies",
                Type = MediaTypes.MovieMediaType,
            }
        );
        ctx.FolderLibrary.Add(new(FolderId, LibraryId));
        ctx.Movies.Add(
            new()
            {
                Id = MovieId,
                Title = "Paper Lanterns",
                TitleSort = "paper lanterns",
                Folder = "/" + MovieFolderName,
                LibraryId = LibraryId,
            }
        );
        ctx.LibraryMovie.Add(new(LibraryId, MovieId));
        ctx.SaveChanges();

        // A system-local driver without a config, resolved the way the job
        // resolves it from the Drivers table.
        Mock<IDriverConfigResolver> resolver = new();
        resolver.Setup(r => r.Resolve(DriverId)).Returns(("local", null));
        _storageFactory = new StorageFactory(
            new LocalStorageDriver(),
            NullLogger<StorageFactory>.Instance,
            resolver.Object
        );
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_scan_stores_the_movie_file_as_one_video_file_row()
    {
        await ScanAsync();

        await using MediaContext ctx = new(_options);
        (await ctx.VideoFiles.CountAsync()).Should().Be(1);

        VideoFile stored = await ctx.VideoFiles.AsNoTracking().SingleAsync();
        stored.Filename.Should().Be("/" + FileName);
        stored.MovieId.Should().Be(MovieId);
    }

    [Fact]
    public async Task A_rescan_of_the_same_file_keeps_one_row_and_its_id()
    {
        await ScanAsync();
        Ulid firstId;
        await using (MediaContext firstScan = new(_options))
        {
            (await firstScan.VideoFiles.CountAsync()).Should().Be(1);
            firstId = (await firstScan.VideoFiles.AsNoTracking().SingleAsync()).Id;
        }

        await ScanAsync();

        await using MediaContext ctx = new(_options);
        (await ctx.VideoFiles.CountAsync()).Should().Be(1);
        (await ctx.VideoFiles.AsNoTracking().SingleAsync()).Id.Should().Be(firstId);
    }

    [Fact]
    public async Task A_write_the_database_refuses_fails_the_scan()
    {
        // Reads keep working, so the scan reaches the write and the write is
        // the one thing that fails.
        await using (SqliteCommand readOnly = _connection.CreateCommand())
        {
            readOnly.CommandText = "PRAGMA query_only = 1;";
            await readOnly.ExecuteNonQueryAsync();
        }

        Func<Task> scan = ScanAsync;

        await scan.Should().ThrowAsync<SqliteException>();
    }

    /// <summary>
    /// One pass of the job's scan for the seeded movie. FileLogic owns and
    /// disposes the context it is handed, as it does in the job.
    /// </summary>
    private async Task ScanAsync()
    {
        MediaContext context = new(_options);
        Library library = await context
            .Libraries.AsNoTracking()
            .Include(l => l.FolderLibraries)
                .ThenInclude(fl => fl.Folder)
            .FirstAsync(l => l.Id == LibraryId);

        await using FileLogic fileLogic = new(
            MovieId,
            library,
            context,
            _storageFactory,
            NullLogger<FileLogic>.Instance
        );
        await fileLogic.Process();
    }
}
