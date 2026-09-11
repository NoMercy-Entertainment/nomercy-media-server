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
using NoMercy.Database;
using NoMercy.MediaProcessing.AudioAnalysis;

namespace NoMercy.Tests.Service.Jobs;

/// <summary>
/// The automix sweep runs <see cref="DjAnalysisQueries.TracksNeedingDjAnalysis" />
/// over the same one-row-per-track tables the base sweep does. An index that
/// exists but is never chosen by the planner buys nothing, so this asks
/// SQLite directly rather than trusting that the migration's indexes are used.
/// <para>
/// Unlike <see cref="AudioAnalysisQueries.TracksNeedingAnalysis" />, the DJ
/// query orders its own result (the brief's given shape ends in
/// <c>.OrderBy(trackId =&gt; trackId)</c>), so even a single, unpaged call
/// plans as a co-routine feeding a temp b-tree. That is an accepted cost, the
/// same one <c>AudioAnalysisQueryPlanTests</c> accepts for its paged case —
/// what these tests hold to is that the two real tables are never scanned,
/// only searched by index.
/// </para>
/// </summary>
public class DjAnalysisQueryPlanTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public DjAnalysisQueryPlanTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private string ExplainNeedsQuery()
    {
        using MediaContext context = new(_options);

        return Explain(
            DjAnalysisQueries
                .TracksNeedingDjAnalysis(context, Ulid.NewUlid(), 1)
                .Take(500)
                .ToQueryString()
        );
    }

    /// <summary>The query a paged host call actually issues: skip, then take.</summary>
    private string ExplainPagedNeedsQuery()
    {
        using MediaContext context = new(_options);

        return Explain(
            DjAnalysisQueries
                .TracksNeedingDjAnalysis(context, Ulid.NewUlid(), 1)
                .Skip(500)
                .Take(500)
                .ToQueryString()
        );
    }

    private string Explain(string sql)
    {
        // ToQueryString prefixes the statement with ".param set" lines; EXPLAIN
        // wants the statement alone.
        string statement = string.Join(
            '\n',
            sql.Split('\n').Where(line => !line.TrimStart().StartsWith(".param"))
        );

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + statement;

        foreach (SqliteParameter parameter in ParametersFor(command.CommandText))
        {
            command.Parameters.Add(parameter);
        }

        List<string> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        return string.Join('\n', rows);
    }

    private static IEnumerable<SqliteParameter> ParametersFor(string sql)
    {
        // Bind whatever the generated SQL asks for so EXPLAIN can prepare.
        // Values do not affect the chosen plan.
        return System
            .Text.RegularExpressions.Regex.Matches(sql, @"@[A-Za-z_][A-Za-z0-9_]*")
            .Select(match => match.Value)
            .Distinct()
            .Select(name => new SqliteParameter(name, 1));
    }

    /// <summary>
    /// The real access path for both TrackDjAnalysis and TrackAudioAnalysis
    /// (searched twice: once for the base verdict, once for the version that
    /// produced the DJ row) is an indexed SEARCH, keyed on TrackId — the
    /// primary key of both tables serves every one of them as a seek.
    /// </summary>
    [Fact]
    public void NeedsQuery_ReachesTheAnalysisTablesByIndex()
    {
        string plan = ExplainNeedsQuery();

        Assert.Contains("SEARCH t USING INDEX sqlite_autoindex_TrackAudioAnalysis_1", plan);
        Assert.Contains("SEARCH t0 USING INDEX sqlite_autoindex_TrackDjAnalysis_1", plan);
        Assert.Contains("SEARCH t1 USING INDEX sqlite_autoindex_TrackAudioAnalysis_1", plan);
    }

    /// <summary>
    /// A scan here is the whole risk: one row per track, run by a sweep. The
    /// only scan the plan may contain is of the co-routine's own ordered
    /// output (<c>l0</c>) — an ephemeral table, not TrackDjAnalysis or
    /// TrackAudioAnalysis.
    /// </summary>
    [Fact]
    public void NeedsQuery_NeverScansEitherAnalysisTable()
    {
        string plan = ExplainNeedsQuery();

        Assert.DoesNotContain("SCAN t0", plan);
        Assert.DoesNotContain("SCAN t1", plan);
        Assert.DoesNotContain("SCAN TrackDjAnalysis", plan);
        Assert.DoesNotContain("SCAN TrackAudioAnalysis", plan);
    }

    [Fact]
    public void NeedsQuery_ReachesTheLibraryJoinByIndex()
    {
        string plan = ExplainNeedsQuery();

        Assert.Contains("SEARCH l USING COVERING INDEX sqlite_autoindex_LibraryTrack_1", plan);
        Assert.DoesNotContain("SCAN LibraryTrack", plan);
    }

    [Fact]
    public void PagedNeedsQuery_StillReachesEveryRealTableByIndex()
    {
        string plan = ExplainPagedNeedsQuery();

        Assert.Contains("SEARCH l USING COVERING INDEX sqlite_autoindex_LibraryTrack_1", plan);
        Assert.Contains("SEARCH t USING INDEX sqlite_autoindex_TrackAudioAnalysis_1", plan);
        Assert.Contains("SEARCH t0 USING INDEX sqlite_autoindex_TrackDjAnalysis_1", plan);
        Assert.Contains("SEARCH t1 USING INDEX sqlite_autoindex_TrackAudioAnalysis_1", plan);
        Assert.DoesNotContain("SCAN t0", plan);
        Assert.DoesNotContain("SCAN t1", plan);
        Assert.DoesNotContain("SCAN LibraryTrack", plan);
        Assert.DoesNotContain("SCAN TrackDjAnalysis", plan);
        Assert.DoesNotContain("SCAN TrackAudioAnalysis", plan);
    }
}
