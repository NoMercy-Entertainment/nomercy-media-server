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

using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Movies;
using NoMercy.MediaProcessing.Jobs.PaletteJobs;

namespace NoMercy.Tests.MediaProcessing.Palettes;

/// <summary>
/// The backfill's needs-palette query decides which rows get a palette job.
/// A failed image download must leave a row it still selects, and the
/// {"poster":{}} shape older builds stored for a failed download must be
/// selected too, so existing libraries heal on the next drain. Runs on real
/// SQLite so the filter is proven to translate, not just evaluate in memory.
/// </summary>
[Trait("Category", "Unit")]
public class PaletteBackfillPendingFilterTests : IDisposable
{
    private const string RealPalette = "{\"poster\":{\"dominant\":\"#abc\",\"primary\":\"#def\"}}";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public PaletteBackfillPendingFilterTests()
    {
        _connection = new("DataSource=:memory:");
        _connection.Open();
        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task Needs_palette_query_selects_blank_and_colourless_rows_only()
    {
        await using MediaContext db = new(_options);
        db.Movies.AddRange(
            Movie(1, null),
            Movie(2, ""),
            Movie(3, "{\"poster\":{}}"),
            Movie(4, "{\"poster\":{},\"backdrop\":{}}"),
            Movie(5, "{}"),
            Movie(6, RealPalette)
        );
        await db.SaveChangesAsync();

        MethodInfo query = typeof(PaletteBackfillJob).GetMethod(
            "GetIntPendingRowsAsync",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        Task<List<(int Id, string? Palette)>> task =
            (Task<List<(int Id, string? Palette)>>)query.Invoke(null, [db, "movie", 0L])!;
        List<(int Id, string? Palette)> rows = await task;

        rows.Select(r => r.Id)
            .Should()
            .BeEquivalentTo(
                [1, 2, 3, 4],
                "blank rows and palettes without colours still need a palette; "
                    + "\"{}\" (no image) and a painted row do not"
            );
    }

    private static Movie Movie(int id, string? palette) =>
        new()
        {
            Id = id,
            Title = $"Movie {id}",
            Poster = "/poster.jpg",
            _colorPalette = palette,
        };
}
