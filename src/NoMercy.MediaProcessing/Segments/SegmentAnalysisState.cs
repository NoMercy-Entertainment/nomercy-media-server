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
/// <see cref="ISegmentAnalysisState"/> in the Configuration table: one
/// comma-separated id list per kind. The version in the key lets a later rule
/// change re-run the backfill everywhere by bumping it.
/// </summary>
public sealed class SegmentAnalysisState(IDbContextFactory<AppDbContext> contextFactory)
    : ISegmentAnalysisState
{
    private const string SeasonsKey = "segment_analysis_v1_seasons";
    private const string MoviesKey = "segment_analysis_v1_movies";

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public Task<HashSet<int>> AnalysedSeasonsAsync(CancellationToken ct) => ReadAsync(SeasonsKey, ct);

    public Task<HashSet<int>> AnalysedMoviesAsync(CancellationToken ct) => ReadAsync(MoviesKey, ct);

    public Task MarkSeasonAsync(int seasonId, CancellationToken ct) => AddAsync(SeasonsKey, seasonId, ct);

    public Task MarkMovieAsync(int movieId, CancellationToken ct) => AddAsync(MoviesKey, movieId, ct);

    private async Task<HashSet<int>> ReadAsync(string key, CancellationToken ct)
    {
        await using AppDbContext db = await contextFactory.CreateDbContextAsync(ct);
        string? value = await db
            .Configuration.AsNoTracking()
            .Where(c => c.Key == key)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
        return Parse(value);
    }

    private async Task AddAsync(string key, int id, CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            await using AppDbContext db = await contextFactory.CreateDbContextAsync(ct);
            HashSet<int> ids = await ReadAsync(key, ct);
            if (!ids.Add(id))
                return;
            await db.SetValueAsync(key, string.Join(',', ids.Order()), ct: ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    internal static HashSet<int> Parse(string? value) =>
        [
            .. (value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1)
                .Where(id => id >= 0),
        ];
}
