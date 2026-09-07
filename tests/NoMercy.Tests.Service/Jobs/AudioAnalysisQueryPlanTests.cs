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
/// The sweep runs once an hour over a table with one row per track. An index
/// that exists in the migration but is not chosen by the planner buys nothing,
/// and "the column is indexed" is not evidence that the query uses it — so this
/// asks SQLite directly.
/// </summary>
public class AudioAnalysisQueryPlanTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AudioAnalysisQueryPlanTests()
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

    private string ExplainSweepQuery()
    {
        using MediaContext context = new(_options);

        string sql = AudioAnalysisQueries
            .TracksNeedingAnalysis(context, [Ulid.NewUlid()], 1)
            .Take(500)
            .ToQueryString();

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
    /// A scan here is the whole risk: one row per track, swept hourly. The
    /// correlated EXISTS is keyed on TrackId, so the primary key serves it.
    /// </summary>
    [Fact]
    public void SweepQuery_NeverScansTheAnalysisTable()
    {
        string plan = ExplainSweepQuery();

        Assert.DoesNotContain("SCAN t", plan);
        Assert.Contains("SEARCH t USING INDEX sqlite_autoindex_TrackAudioAnalysis_1", plan);
    }

    [Fact]
    public void SweepQuery_NeverScansTheLibraryJoin()
    {
        string plan = ExplainSweepQuery();

        Assert.DoesNotContain("SCAN l", plan);
    }
}
