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

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using Xunit;
using Xunit.Abstractions;

namespace NoMercy.Tests.Api.Media;

/// <summary>
/// Times the anime themes grid against a real library, read-only, split into
/// the database, the mapping and the JSON. Runs only when NOMERCY_PERF_DB names
/// a media.db; the suite has no library of that size.
/// </summary>
[Trait("Category", "Perf")]
public partial class AnimeThemesResponseTimeTests(ITestOutputHelper output)
{
    private const int WarmRuns = 3;
    private const int TimedRuns = 20;

    // The budget Stoney set for the warm grid: the page must feel instant.
    private const double BudgetMs = 97;

    private static readonly JsonSerializerSettings ApiJson = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        Converters = [new StringEnumConverter()],
    };

    [GeneratedRegex(@"Executed DbCommand \((\d+)ms\)")]
    private static partial Regex ExecutedCommand();

    [GeneratedRegex(@"spending (\d+)ms reading results")]
    private static partial Regex ReaderDisposed();

    [Fact]
    public async Task Themes_grid_answers_within_budget_on_a_real_library()
    {
        string? dbPath = Environment.GetEnvironmentVariable("NOMERCY_PERF_DB");
        if (string.IsNullOrEmpty(dbPath))
        {
            output.WriteLine("NOMERCY_PERF_DB is not set; nothing measured.");
            return;
        }

        List<double> dbMs = [];
        double runDbMs = 0;
        SqliteConnectionStringBuilder connection = new()
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(connection.ToString())
            .LogTo(
                line =>
                {
                    Match match = ExecutedCommand().Match(line);
                    if (match.Success)
                        runDbMs += double.Parse(match.Groups[1].Value);
                    Match reader = ReaderDisposed().Match(line);
                    if (reader.Success)
                        runDbMs += double.Parse(reader.Groups[1].Value);
                    if (Environment.GetEnvironmentVariable("NOMERCY_PERF_SQL") == "1")
                        output.WriteLine(line);
                },
                [
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted,
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.DataReaderDisposing,
                ]
            )
            .Options;

        await using MediaContext context = new(options);
        Guid userId = await context
            .LibraryUser.AsNoTracking()
            .GroupBy(user => user.UserId)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstAsync();
        AnimeThemeRepository repository = new(context);

        List<double> totalMs = [];
        List<double> queryMs = [];
        List<double> mapMs = [];
        List<double> jsonMs = [];
        int bytes = 0;
        int cards = 0;
        int posters = 0;

        for (int run = 0; run < WarmRuns + TimedRuns; run++)
        {
            runDbMs = 0;
            Stopwatch total = Stopwatch.StartNew();
            List<AnimeThemeWithCountsDto> themes = await repository.GetThemesWithCountsAsync(
                userId,
                "en",
                300,
                0,
                CancellationToken.None
            );
            double afterQuery = total.Elapsed.TotalMilliseconds;

            List<GroupCardData> cardData =
            [
                .. themes
                    .Where(t => t.TvShowsWithVideo > 0 || t.MoviesWithVideo > 0)
                    .Select(dto => new GroupCardData(dto)),
            ];
            ComponentResponse response = ComponentResponse.From(
                Component
                    .Grid()
                    .WithId("anime-themes")
                    .WithItems(cardData.Select(card => Component.GroupCard().WithData(card)))
            );
            double afterMap = total.Elapsed.TotalMilliseconds;

            string json = JsonConvert.SerializeObject(response, ApiJson);
            double afterJson = total.Elapsed.TotalMilliseconds;

            if (run < WarmRuns)
                continue;

            totalMs.Add(afterJson);
            queryMs.Add(afterQuery);
            dbMs.Add(runDbMs);
            mapMs.Add(afterMap - afterQuery);
            jsonMs.Add(afterJson - afterMap);
            bytes = Encoding.UTF8.GetByteCount(json);
            cards = cardData.Count;
            posters = cardData.Sum(card => card.ItemPosters.Length);
        }

        output.WriteLine(
            $"cards={cards} posters={posters} bytes={bytes} "
                + $"total={Median(totalMs):F1}ms query={Median(queryMs):F1}ms "
                + $"(sqlite={Median(dbMs):F1}ms) map={Median(mapMs):F1}ms json={Median(jsonMs):F1}ms "
                + $"min={totalMs.Min():F1}ms max={totalMs.Max():F1}ms"
        );

        Assert.True(
            Median(totalMs) <= BudgetMs,
            $"warm median {Median(totalMs):F1}ms is over the {BudgetMs}ms budget"
        );
    }

    private static double Median(List<double> values)
    {
        List<double> sorted = [.. values.Order()];
        return sorted[sorted.Count / 2];
    }
}
