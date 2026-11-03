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
using NoMercy.Database.Models.Common;

namespace NoMercy.MediaProcessing.Shows;

/// <summary>
/// Reads and writes the backfill cursor, unanswered-title list and completion flag for
/// <see cref="AnimeEnrichmentBackfillJob"/> in the <see cref="AppDbContext"/>
/// Configuration table, mirroring <c>PaletteBackfillState</c>'s pattern: a
/// per-entity-type cursor lets the drain resume after a restart instead of
/// re-walking titles it already enriched.
/// </summary>
public class AnimeEnrichmentBackfillState
{
    private const string CompleteKey = "anime_enrichment_backfill_complete";

    private static string CursorKey(string entityType) =>
        $"anime_enrichment_backfill_cursor_{entityType}";

    private static string UnansweredKey(string entityType) =>
        $"anime_enrichment_backfill_unanswered_{entityType}";

    public static async Task<bool> IsCompleteAsync(AppDbContext db, CancellationToken ct)
    {
        string? value = await db
            .Configuration.Where(c => c.Key == CompleteKey)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
        return value == "true";
    }

    public static async Task SetCompleteAsync(AppDbContext db, CancellationToken ct)
    {
        await UpsertConfigAsync(db, CompleteKey, "true", ct);
    }

    public static async Task<int> GetCursorAsync(
        AppDbContext db,
        string entityType,
        CancellationToken ct
    )
    {
        string key = CursorKey(entityType);
        string? value = await db
            .Configuration.Where(c => c.Key == key)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
        return value is null ? 0 : int.Parse(value);
    }

    public static async Task SetCursorAsync(
        AppDbContext db,
        string entityType,
        int cursor,
        CancellationToken ct
    )
    {
        await UpsertConfigAsync(db, CursorKey(entityType), cursor.ToString(), ct);
    }

    /// <summary>
    /// Ids of the titles the providers could not answer (an outage, a timeout, a
    /// rate limit), in ascending order. The cursor has moved past them; this list
    /// is what keeps them from being skipped for good.
    /// </summary>
    public static async Task<List<int>> GetUnansweredIdsAsync(
        AppDbContext db,
        string entityType,
        CancellationToken ct
    )
    {
        string key = UnansweredKey(entityType);
        string? value = await db
            .Configuration.Where(c => c.Key == key)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(value))
            return [];

        return value.Split(',').Select(int.Parse).Order().ToList();
    }

    public static async Task SetUnansweredIdsAsync(
        AppDbContext db,
        string entityType,
        IEnumerable<int> ids,
        CancellationToken ct
    )
    {
        string value = string.Join(',', ids.Distinct().Order());
        await UpsertConfigAsync(db, UnansweredKey(entityType), value, ct);
    }

    private static async Task UpsertConfigAsync(
        AppDbContext db,
        string key,
        string value,
        CancellationToken ct
    )
    {
        await db.SetValueAsync(key, value, ct: ct);
    }
}
