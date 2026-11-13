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
using Moq;
using NoMercy.Database;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// The mass-missing guard counts titles from this query, so it must return every
/// registered path of the asked library, keyed by title, and nothing of another library.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RecordedVideoFileLocationsByLibraryTests : IDisposable
{
    private static readonly Ulid LibraryId = Ulid.NewUlid();
    private static readonly Ulid OtherLibraryId = Ulid.NewUlid();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public RecordedVideoFileLocationsByLibraryTests()
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
    }

    public void Dispose() => _connection.Dispose();

    private static Database.Models.Media.VideoFile VideoFileOf(
        int? movieId,
        int? episodeId,
        string hostFolder
    ) =>
        new()
        {
            MovieId = movieId,
            EpisodeId = episodeId,
            Share = "share",
            HostFolder = hostFolder,
            Filename = "/file.m3u8",
            Quality = "1080",
            Languages = "[]",
        };

    [Fact]
    public async Task MovieLibrary_ReturnsItsTitlesOnly_KeyedByMovieId()
    {
        await using (MediaContext seed = new(_options))
        {
            seed.Movies.AddRange(
                new() { Id = 1, LibraryId = LibraryId },
                new() { Id = 2, LibraryId = LibraryId },
                new() { Id = 3, LibraryId = OtherLibraryId }
            );
            seed.VideoFiles.AddRange(
                VideoFileOf(1, null, "/films/one"),
                VideoFileOf(1, null, "/films/one-extended"),
                VideoFileOf(3, null, "/other/three")
            );
            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Mock.Of<IStorageDriver>());

        Dictionary<int, List<RecordedVideoFileLocation>> result =
            await repository.GetRecordedVideoFileLocationsByLibraryAsync(
                LibraryId,
                MediaTypes.MovieMediaType
            );

        result.Keys.Should().BeEquivalentTo([1], "movie 2 has no file and movie 3 is elsewhere");
        result[1]
            .Select(location => location.HostFolder)
            .Should()
            .BeEquivalentTo("/films/one", "/films/one-extended");
    }

    [Fact]
    public async Task TvLibrary_ReturnsEpisodeFilesKeyedByShowId()
    {
        await using (MediaContext seed = new(_options))
        {
            seed.Tvs.AddRange(
                new() { Id = 10, LibraryId = LibraryId },
                new() { Id = 20, LibraryId = OtherLibraryId }
            );
            seed.Episodes.AddRange(
                new()
                {
                    Id = 101,
                    TvId = 10,
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                },
                new()
                {
                    Id = 102,
                    TvId = 10,
                    SeasonNumber = 1,
                    EpisodeNumber = 2,
                },
                new()
                {
                    Id = 201,
                    TvId = 20,
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                }
            );
            seed.VideoFiles.AddRange(
                VideoFileOf(null, 101, "/shows/ten/e01"),
                VideoFileOf(null, 102, "/shows/ten/e02"),
                VideoFileOf(null, 201, "/other/twenty/e01")
            );
            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Mock.Of<IStorageDriver>());

        Dictionary<int, List<RecordedVideoFileLocation>> result =
            await repository.GetRecordedVideoFileLocationsByLibraryAsync(
                LibraryId,
                MediaTypes.TvMediaType
            );

        result.Keys.Should().BeEquivalentTo([10]);
        result[10]
            .Select(location => location.HostFolder)
            .Should()
            .BeEquivalentTo("/shows/ten/e01", "/shows/ten/e02");
    }
}
