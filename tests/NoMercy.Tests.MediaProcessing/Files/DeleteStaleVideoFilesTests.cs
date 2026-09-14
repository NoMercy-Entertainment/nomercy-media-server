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
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Files;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// A rescan must never wipe a folder it did not fully verify this pass — the
/// unconditional delete FindFiles used to run before FileRepository even
/// touched storage dropped an entire show's registrations whenever a single
/// folder failed to enumerate or a single item failed to resolve (issue #55).
/// The narrower delete these methods run only removes a row when its Share
/// was both scanned successfully this pass AND not among what the pass
/// re-stored; every other row is left exactly as it was.
/// </summary>
public class DeleteStaleVideoFilesTests : IDisposable
{
    private static IStorageDriver Driver => new Mock<IStorageDriver>(MockBehavior.Loose).Object;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public DeleteStaleVideoFilesTests()
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

    private async Task<List<VideoFile>> AllVideoFilesAsync()
    {
        await using MediaContext context = new(_options);
        return await context.VideoFiles.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Tv_DeletesOnlyTheStaleRowInAnEligibleShare_PreservesTheIneligibleShareEntirely()
    {
        const int tvId = 100;

        await using (MediaContext seed = new(_options))
        {
            seed.Episodes.AddRange(
                new Episode
                {
                    Id = 1,
                    EpisodeNumber = 1,
                    SeasonNumber = 1,
                    TvId = tvId,
                },
                new Episode
                {
                    Id = 2,
                    EpisodeNumber = 2,
                    SeasonNumber = 1,
                    TvId = tvId,
                }
            );

            // shareA/stale: this pass re-scanned shareA successfully but this
            // particular file is no longer on disk — a genuine stale row that
            // must be reconciled away.
            seed.VideoFiles.Add(
                new()
                {
                    EpisodeId = 1,
                    Share = "shareA",
                    HostFolder = "/A/ep1-old",
                    Filename = "/ep1-old.mkv",
                    Quality = "1080",
                    Languages = "[]",
                }
            );

            // shareA/current: still exactly what this pass re-stored — must
            // survive.
            seed.VideoFiles.Add(
                new()
                {
                    EpisodeId = 1,
                    Share = "shareA",
                    HostFolder = "/A/ep1-current",
                    Filename = "/ep1-current.mkv",
                    Quality = "1080",
                    Languages = "[]",
                }
            );

            // shareB: its folder was skipped/failed this pass, so it never
            // made eligibleShares — every one of its rows must be left alone,
            // even though nothing from it was re-stored either.
            seed.VideoFiles.Add(
                new()
                {
                    EpisodeId = 2,
                    Share = "shareB",
                    HostFolder = "/B/ep2",
                    Filename = "/ep2.mkv",
                    Quality = "1080",
                    Languages = "[]",
                }
            );

            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        await repository.DeleteStaleVideoFilesAndMetadataByTvIdAsync(
            tvId,
            ["shareA"],
            [new("shareA", "/A/ep1-current", "/ep1-current.mkv")]
        );

        List<VideoFile> remaining = await AllVideoFilesAsync();

        remaining.Should().HaveCount(2);
        remaining.Should().ContainSingle(vf => vf.Filename == "/ep1-current.mkv");
        remaining
            .Should()
            .ContainSingle(vf => vf.Filename == "/ep2.mkv", "shareB was never scanned this pass");
        remaining.Should().NotContain(vf => vf.Filename == "/ep1-old.mkv");
    }

    [Fact]
    public async Task Tv_NoEligibleShares_DeletesNothing()
    {
        const int tvId = 101;

        await using (MediaContext seed = new(_options))
        {
            seed.Episodes.Add(
                new()
                {
                    Id = 3,
                    EpisodeNumber = 1,
                    SeasonNumber = 1,
                    TvId = tvId,
                }
            );
            seed.VideoFiles.Add(
                new()
                {
                    EpisodeId = 3,
                    Share = "shareC",
                    HostFolder = "/C/ep1",
                    Filename = "/ep1.mkv",
                    Quality = "1080",
                    Languages = "[]",
                }
            );
            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        await repository.DeleteStaleVideoFilesAndMetadataByTvIdAsync(tvId, [], []);

        (await AllVideoFilesAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Movie_DeletesOnlyTheStaleRowInAnEligibleShare_PreservesTheIneligibleShareEntirely()
    {
        const int movieId = 200;

        await using (MediaContext seed = new(_options))
        {
            seed.VideoFiles.Add(
                new()
                {
                    MovieId = movieId,
                    Share = "shareA",
                    HostFolder = "/A/movie-old",
                    Filename = "/movie-old.mkv",
                    Quality = "1080",
                    Languages = "[]",
                }
            );
            seed.VideoFiles.Add(
                new()
                {
                    MovieId = movieId,
                    Share = "shareB",
                    HostFolder = "/B/movie",
                    Filename = "/movie.mkv",
                    Quality = "1080",
                    Languages = "[]",
                }
            );
            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        await repository.DeleteStaleVideoFilesAndMetadataByMovieIdAsync(movieId, ["shareA"], []);

        List<VideoFile> remaining = await AllVideoFilesAsync();
        remaining.Should().ContainSingle();
        remaining[0].Filename.Should().Be("/movie.mkv");
    }

    [Fact]
    public async Task DeletingAStaleRow_AlsoDeletesItsMetadata()
    {
        const int movieId = 300;
        Metadata metadata = new() { Filename = "/movie-old.mkv", HostFolder = "/A/movie-old" };

        await using (MediaContext seed = new(_options))
        {
            seed.Metadata.Add(metadata);
            seed.VideoFiles.Add(
                new()
                {
                    MovieId = movieId,
                    Share = "shareA",
                    HostFolder = "/A/movie-old",
                    Filename = "/movie-old.mkv",
                    Quality = "1080",
                    Languages = "[]",
                    MetadataId = metadata.Id,
                }
            );
            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        await repository.DeleteStaleVideoFilesAndMetadataByMovieIdAsync(movieId, ["shareA"], []);

        await using MediaContext read = new(_options);
        (await read.Metadata.AsNoTracking().AnyAsync(m => m.Id == metadata.Id)).Should().BeFalse();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
