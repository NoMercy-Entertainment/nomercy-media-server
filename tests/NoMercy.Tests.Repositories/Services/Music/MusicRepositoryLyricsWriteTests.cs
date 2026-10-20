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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Music;

namespace NoMercy.Tests.Repositories.Services.Music;

/// <summary>
/// Every write to the lyrics of a track records where the text and the timing
/// came from and bumps the version, so a later writer can tell that someone
/// wrote in between (#72, groundwork for #620).
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicRepositoryLyricsWriteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Track _track = new()
    {
        Id = Guid.NewGuid(),
        Name = "Track",
        Duration = "03:45",
        FolderId = Ulid.NewUlid(),
    };

    public MusicRepositoryLyricsWriteTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
        context.Tracks.Add(_track);
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Every_lyrics_write_records_provenance_and_bumps_the_version()
    {
        MusicRepository repository = new(CreateFactory());

        await repository.UpdateTrackLyricsAsync(
            _track,
            """[{"text":"line one","time":{"total":10.0}}]""",
            LyricsSources.Lrclib,
            LyricsTimings.Provider
        );
        Track afterFetch = await ReadTrack();
        afterFetch.Lyrics.Should().ContainSingle().Which.Text.Should().Be("line one");
        afterFetch.LyricsSource.Should().Be(LyricsSources.Lrclib);
        afterFetch.LyricsTiming.Should().Be(LyricsTimings.Provider);
        afterFetch.LyricsVersion.Should().Be(1);

        await repository.UpdateTrackLyricsOffsetAsync(_track, 500);
        Track afterOffset = await ReadTrack();
        afterOffset.LyricsOffset.Should().Be(500);
        afterOffset
            .LyricsSource.Should()
            .Be(LyricsSources.Lrclib, "the offset does not touch provenance");
        afterOffset.LyricsVersion.Should().Be(2);

        await repository.UpdateTrackLyricsAsync(_track, "[]", null, null);
        Track afterNegative = await ReadTrack();
        afterNegative.Lyrics.Should().BeEmpty();
        afterNegative.LyricsSource.Should().BeNull("the negative marker has no provenance");
        afterNegative.LyricsTiming.Should().BeNull();
        afterNegative.LyricsVersion.Should().Be(3);
    }

    private async Task<Track> ReadTrack()
    {
        await using MediaContext context = new(_options);
        return await context.Tracks.AsNoTracking().SingleAsync(t => t.Id == _track.Id);
    }

    private IDbContextFactory<MediaContext> CreateFactory()
    {
        Mock<IDbContextFactory<MediaContext>> mock = new();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
        return mock.Object;
    }
}
