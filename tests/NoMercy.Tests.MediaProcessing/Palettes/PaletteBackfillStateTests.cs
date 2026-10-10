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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.MediaProcessing.Images.Palettes;
using NoMercy.MediaProcessing.Jobs.PaletteJobs;

namespace NoMercy.Tests.MediaProcessing.Palettes;

[Trait("Category", "Unit")]
public class PaletteBackfillStateTests : IDisposable
{
    private readonly AppDbContext _db;

    public PaletteBackfillStateTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new(options);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task IsComplete_returns_false_when_flag_not_set()
    {
        bool complete = await PaletteBackfillState.IsCompleteAsync(_db, CancellationToken.None);
        complete.Should().BeFalse();
    }

    [Fact]
    public async Task SetComplete_then_IsComplete_returns_true()
    {
        await PaletteBackfillState.SetCompleteAsync(_db, CancellationToken.None);
        bool complete = await PaletteBackfillState.IsCompleteAsync(_db, CancellationToken.None);
        complete.Should().BeTrue();
    }

    [Fact]
    public async Task GetCursor_defaults_to_zero_when_not_set()
    {
        long cursor = await PaletteBackfillState.GetCursorAsync(
            _db,
            "movie",
            CancellationToken.None
        );
        cursor.Should().Be(0L);
    }

    [Fact]
    public async Task SetCursor_then_GetCursor_round_trips()
    {
        await PaletteBackfillState.SetCursorAsync(_db, "movie", 42L, CancellationToken.None);
        long cursor = await PaletteBackfillState.GetCursorAsync(
            _db,
            "movie",
            CancellationToken.None
        );
        cursor.Should().Be(42L);
    }

    [Fact]
    public async Task SetCursor_overwrites_previous_value()
    {
        await PaletteBackfillState.SetCursorAsync(_db, "tv", 10L, CancellationToken.None);
        await PaletteBackfillState.SetCursorAsync(_db, "tv", 99L, CancellationToken.None);
        long cursor = await PaletteBackfillState.GetCursorAsync(_db, "tv", CancellationToken.None);
        cursor.Should().Be(99L);
    }

    [Fact]
    public async Task Cursors_for_different_entity_types_are_independent()
    {
        await PaletteBackfillState.SetCursorAsync(_db, "movie", 100L, CancellationToken.None);
        await PaletteBackfillState.SetCursorAsync(_db, "tv", 200L, CancellationToken.None);

        long movieCursor = await PaletteBackfillState.GetCursorAsync(
            _db,
            "movie",
            CancellationToken.None
        );
        long tvCursor = await PaletteBackfillState.GetCursorAsync(
            _db,
            "tv",
            CancellationToken.None
        );

        movieCursor.Should().Be(100L);
        tvCursor.Should().Be(200L);
    }

    [Fact]
    public async Task Current_version_reopens_completed_v2_backfill_and_resets_guid_cursor()
    {
        await PaletteBackfillState.EnsureVersionAsync(
            _db,
            2,
            PaletteBackfillJob.AllTypes,
            CancellationToken.None
        );
        await PaletteBackfillState.SetCursorAsync(_db, "track", 200L, CancellationToken.None);
        await PaletteBackfillState.SetCompleteAsync(_db, CancellationToken.None);

        bool reopened = await PaletteBackfillState.EnsureVersionAsync(
            _db,
            PaletteBackfillJob.CurrentVersion,
            PaletteBackfillJob.AllTypes,
            CancellationToken.None
        );

        reopened.Should().BeTrue();
        (await PaletteBackfillState.IsCompleteAsync(_db, CancellationToken.None))
            .Should()
            .BeFalse();
        (await PaletteBackfillState.GetCursorAsync(_db, "track", CancellationToken.None))
            .Should()
            .Be(0L);
    }
}
