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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// Deleting a title takes its subtree with it.
///
/// <para>
/// Foreign-key enforcement is left ON here, unlike the other fixtures in this
/// project. That is the whole point: the delete this covers used to switch
/// enforcement off, which did not make it complete, only silent. A test with the
/// constraint disabled would agree the old delete worked.
/// </para>
/// </summary>
public class MediaSubtreeDeleteTests : IDisposable
{
    private const int ShowId = 138502;
    private const int OtherShowId = 203744;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public MediaSubtreeDeleteTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        using SqliteCommand enforce = _connection.CreateCommand();
        enforce.CommandText = "PRAGMA foreign_keys = ON;";
        enforce.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ADeletedShowLeavesNothingBehind()
    {
        await SeedShowAsync(ShowId, "X-Men '97");

        await using (MediaContext context = new(_options))
        {
            await MediaSubtreeDelete.ShowAsync(context, ShowId);
        }

        await using MediaContext after = new(_options);

        after.Tvs.Count(tv => tv.Id == ShowId).Should().Be(0, "the show itself is gone");

        // The row the library counts. Left behind, it made the library list a
        // show that no longer existed.
        after.LibraryTv.Count(row => row.TvId == ShowId).Should().Be(0);

        after.Seasons.Count(season => season.TvId == ShowId).Should().Be(0);
        after.Episodes.Count(episode => episode.TvId == ShowId).Should().Be(0);
        after.Translations.Count(row => row.TvId == ShowId).Should().Be(0);
        after.Images.Count(row => row.TvId == ShowId).Should().Be(0);
        after.Casts.Count(row => row.TvId == ShowId).Should().Be(0);
        after.Crews.Count(row => row.TvId == ShowId).Should().Be(0);
        after.Medias.Count(row => row.TvId == ShowId).Should().Be(0);
        after.AlternativeTitles.Count(row => row.TvId == ShowId).Should().Be(0);
    }

    /// <summary>
    /// The delete is keyed on this show's own ids, so the show beside it is
    /// untouched. The shared tables are the reason this is asserted: a row
    /// belonging to another title carries a null TvId, and a delete that read
    /// null as a match would take the lot.
    /// </summary>
    [Fact]
    public async Task ADeletedShowLeavesTheShowBesideItAlone()
    {
        await SeedShowAsync(ShowId, "X-Men '97");
        await SeedShowAsync(OtherShowId, "Sugar");

        await using (MediaContext context = new(_options))
        {
            await MediaSubtreeDelete.ShowAsync(context, ShowId);
        }

        await using MediaContext after = new(_options);

        after.Tvs.Count(tv => tv.Id == OtherShowId).Should().Be(1);
        after.Episodes.Count(episode => episode.TvId == OtherShowId).Should().Be(2);
        after.Seasons.Count(season => season.TvId == OtherShowId).Should().Be(1);
        after.LibraryTv.Count(row => row.TvId == OtherShowId).Should().Be(1);
        after.Translations.Count(row => row.TvId == OtherShowId).Should().Be(1);
        after.Images.Count(row => row.TvId == OtherShowId).Should().Be(1);
    }

    /// <summary>
    /// Nothing anywhere is left pointing at a parent that is gone.
    ///
    /// <para>
    /// Asked of the database rather than of a list written here. A hand-kept
    /// list of tables is the thing that goes stale the day a table is added, and
    /// a delete that misses a new table looks exactly like a delete that works.
    /// <c>PRAGMA foreign_key_check</c> walks every declared foreign key in the
    /// schema and reports every row violating one, so a table added later is
    /// covered the day it appears.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NoRowAnywhereIsLeftPointingAtADeletedShow()
    {
        await SeedShowAsync(ShowId, "X-Men '97");
        await SeedShowAsync(OtherShowId, "Sugar");

        await using (MediaContext context = new(_options))
        {
            await MediaSubtreeDelete.ShowAsync(context, ShowId);
        }

        List<string> violations = [];

        await using (SqliteCommand check = _connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check";

            await using SqliteDataReader reader = await check.ExecuteReaderAsync();

            while (await reader.ReadAsync())
                violations.Add(
                    $"{reader.GetString(0)} row {reader.GetValue(1)} -> {reader.GetString(2)}"
                );
        }

        violations.Should().BeEmpty("a delete that leaves a row behind is an incomplete delete");
    }

    private async Task SeedShowAsync(int id, string title)
    {
        await using MediaContext context = new(_options);

        Library library =
            await context.Libraries.FirstOrDefaultAsync()
            ?? context
                .Libraries.Add(
                    new()
                    {
                        Id = Ulid.NewUlid(),
                        Title = "Television",
                        Type = "tv",
                    }
                )
                .Entity;

        await context.SaveChangesAsync();

        // Tv.LibraryId is required: a show belongs to a library from the moment
        // it exists.
        context.Tvs.Add(
            new()
            {
                Id = id,
                Title = title,
                LibraryId = library.Id,
            }
        );
        await context.SaveChangesAsync();

        Season season = new()
        {
            Id = id + 1,
            TvId = id,
            SeasonNumber = 1,
            Title = "Season 1",
        };
        context.Seasons.Add(season);
        await context.SaveChangesAsync();

        context.Episodes.AddRange(
            new()
            {
                Id = id + 10,
                TvId = id,
                SeasonId = season.Id,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                Title = "One",
            },
            new()
            {
                Id = id + 11,
                TvId = id,
                SeasonId = season.Id,
                SeasonNumber = 1,
                EpisodeNumber = 2,
                Title = "Two",
            }
        );

        context.LibraryTv.Add(new() { TvId = id, LibraryId = library.Id });
        // Cast and crew are the bulk of what one show hangs off - 440 of the 723
        // rows one delete left behind - and they need the person they name.
        context.People.Add(new() { Id = id, Name = title });
        await context.SaveChangesAsync();

        context.Casts.Add(new() { TvId = id, PersonId = id });
        context.Crews.Add(new() { TvId = id, PersonId = id });
        context.Medias.Add(
            new()
            {
                Id = Ulid.NewUlid(),
                TvId = id,
                Src = $"/{id}.mp4",
                Type = "video",
            }
        );
        context.Translations.Add(
            new()
            {
                TvId = id,
                Iso6391 = "en",
                Title = title,
            }
        );
        context.Images.Add(
            new()
            {
                TvId = id,
                FilePath = $"/{id}.jpg",
                Type = "poster",
            }
        );
        context.AlternativeTitles.Add(
            new()
            {
                TvId = id,
                Title = title,
                Iso31661 = "US",
            }
        );

        await context.SaveChangesAsync();
    }
}
