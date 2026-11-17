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

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;

namespace NoMercy.MediaProcessing.Segments;

/// <summary>
/// <see cref="ISegmentAnalysisState"/> in the Configuration table: per target
/// one comma-separated list of analysed ids and one of <c>id:failures</c>. The
/// version in the keys lets a later rule change re-run the backfill everywhere.
/// </summary>
public sealed class SegmentAnalysisState(IDbContextFactory<AppDbContext> contextFactory)
    : ISegmentAnalysisState
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public async Task<HashSet<int>> AnalysedAsync(
        SegmentAnalysisTarget target,
        CancellationToken ct
    ) => Parse(await ReadAsync(AnalysedKey(target), ct));

    public async Task<HashSet<int>> GivenUpAsync(
        SegmentAnalysisTarget target,
        CancellationToken ct
    ) =>
        [
            .. ParseCounts(await ReadAsync(FailuresKey(target), ct))
                .Where(f => f.Value >= ISegmentAnalysisState.MaxFailures)
                .Select(f => f.Key),
        ];

    public Task MarkAnalysedAsync(SegmentAnalysisTarget target, int id, CancellationToken ct) =>
        WriteAsync(
            async db =>
            {
                HashSet<int> ids = Parse(await ReadAsync(AnalysedKey(target), ct));
                if (ids.Add(id))
                    await db.SetValueAsync(
                        AnalysedKey(target),
                        string.Join(',', ids.Order()),
                        ct: ct
                    );
                await SetFailuresAsync(db, target, id, 0, ct);
                return 0;
            },
            ct
        );

    public Task<int> RecordFailureAsync(
        SegmentAnalysisTarget target,
        int id,
        CancellationToken ct
    ) =>
        WriteAsync(
            async db =>
            {
                Dictionary<int, int> counts = ParseCounts(await ReadAsync(FailuresKey(target), ct));
                int count = counts.GetValueOrDefault(id) + 1;
                await SetFailuresAsync(db, target, id, count, ct);
                return count;
            },
            ct
        );

    public Task ResetFailuresAsync(SegmentAnalysisTarget target, int id, CancellationToken ct) =>
        WriteAsync(
            async db =>
            {
                await SetFailuresAsync(db, target, id, 0, ct);
                return 0;
            },
            ct
        );

    internal static HashSet<int> Parse(string? value) =>
        [
            .. (value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v =>
                    int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                        ? id
                        : -1
                )
                .Where(id => id >= 0),
        ];

    internal static Dictionary<int, int> ParseCounts(string? value)
    {
        Dictionary<int, int> counts = new();
        foreach (
            string entry in (value ?? string.Empty).Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            string[] parts = entry.Split(':');
            if (
                parts.Length == 2
                && int.TryParse(
                    parts[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int id
                )
                && int.TryParse(
                    parts[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int count
                )
            )
                counts[id] = count;
        }

        return counts;
    }

    private static string AnalysedKey(SegmentAnalysisTarget target) =>
        target == SegmentAnalysisTarget.Season
            ? "segment_analysis_v1_seasons"
            : "segment_analysis_v1_movies";

    private static string FailuresKey(SegmentAnalysisTarget target) =>
        target == SegmentAnalysisTarget.Season
            ? "segment_analysis_v1_season_failures"
            : "segment_analysis_v1_movie_failures";

    private async Task SetFailuresAsync(
        AppDbContext db,
        SegmentAnalysisTarget target,
        int id,
        int count,
        CancellationToken ct
    )
    {
        Dictionary<int, int> counts = ParseCounts(await ReadAsync(FailuresKey(target), ct));
        if (count == 0 && !counts.ContainsKey(id))
            return;
        if (count == 0)
            counts.Remove(id);
        else
            counts[id] = count;
        await db.SetValueAsync(
            FailuresKey(target),
            string.Join(',', counts.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value}")),
            ct: ct
        );
    }

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        await using AppDbContext db = await contextFactory.CreateDbContextAsync(ct);
        return await db
            .Configuration.AsNoTracking()
            .Where(c => c.Key == key)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<T> WriteAsync<T>(Func<AppDbContext, Task<T>> write, CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            await using AppDbContext db = await contextFactory.CreateDbContextAsync(ct);
            return await write(db);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
