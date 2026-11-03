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
using Microsoft.EntityFrameworkCore.Diagnostics;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.MediaProcessing.Shows;

namespace NoMercy.Tests.MediaProcessing.Shows;

/// <summary>
/// Two writers of the same Configuration key (two backfill jobs on the extras
/// queue) must not break each other with a unique-key violation.
/// </summary>
[Trait("Category", "Unit")]
public class AnimeEnrichmentBackfillStateRaceTests : IDisposable
{
    private const string CompleteKey = "anime_enrichment_backfill_complete";
    private const string CursorKey = "anime_enrichment_backfill_cursor_movie";
    private const string UnansweredKey = "anime_enrichment_backfill_unanswered_movie";

    private readonly SqliteConnection _connection;

    public AnimeEnrichmentBackfillStateRaceTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using AppDbContext setup = new(PlainOptions());
        setup.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SetComplete_survives_a_competing_insert_of_the_same_key()
    {
        await using AppDbContext db = new(RacingOptions(CompleteKey, "false"));

        Func<Task> act = () => AnimeEnrichmentBackfillState.SetCompleteAsync(db, default);

        await act.Should().NotThrowAsync();
        (await ValueOfAsync(CompleteKey)).Should().Be("true");
    }

    [Fact]
    public async Task SetCursor_survives_a_competing_insert_of_the_same_key()
    {
        await using AppDbContext db = new(RacingOptions(CursorKey, "1"));

        Func<Task> act = () => AnimeEnrichmentBackfillState.SetCursorAsync(db, "movie", 7, default);

        await act.Should().NotThrowAsync();
        (await ValueOfAsync(CursorKey)).Should().Be("7");
    }

    [Fact]
    public async Task SetUnanswered_survives_a_competing_insert_of_the_same_key()
    {
        await using AppDbContext db = new(RacingOptions(UnansweredKey, "1"));

        Func<Task> act = () =>
            AnimeEnrichmentBackfillState.SetUnansweredIdsAsync(db, "movie", [3, 2], default);

        await act.Should().NotThrowAsync();
        (await ValueOfAsync(UnansweredKey)).Should().Be("2,3");
    }

    [Fact]
    public async Task Two_concurrent_callers_leave_one_row_with_a_value()
    {
        await using AppDbContext first = new(PlainOptions());
        await using AppDbContext second = new(PlainOptions());

        await AnimeEnrichmentBackfillState.SetCursorAsync(first, "movie", 4, default);
        await AnimeEnrichmentBackfillState.SetCursorAsync(second, "movie", 9, default);

        (await ValueOfAsync(CursorKey)).Should().Be("9");
    }

    private async Task<string?> ValueOfAsync(string key)
    {
        await using AppDbContext db = new(PlainOptions());
        List<Configuration> rows = await db.Configuration.Where(c => c.Key == key).ToListAsync();
        rows.Should().ContainSingle();
        return rows[0].Value;
    }

    private DbContextOptions<AppDbContext> PlainOptions()
    {
        return new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    // Inserts the key from another context right before the first save, which
    // is the window between the upsert's read and its insert.
    private DbContextOptions<AppDbContext> RacingOptions(string key, string rivalValue)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new CompetingInsertInterceptor(PlainOptions(), key, rivalValue))
            .Options;
    }

    private sealed class CompetingInsertInterceptor(
        DbContextOptions<AppDbContext> rivalOptions,
        string key,
        string value
    ) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (_fired)
                return result;

            _fired = true;
            await using AppDbContext rival = new(rivalOptions);
            rival.Configuration.Add(new() { Key = key, Value = value });
            await rival.SaveChangesAsync(cancellationToken);
            return result;
        }
    }
}
