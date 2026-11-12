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

using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem.Information;
using NoMercy.Providers.AniList;
using NoMercy.Providers.Jikan;

namespace NoMercy.Benchmarks;

// Creates a MediaContext bound to the real database. MediaContext.OnConfiguring
// self-configures from AppFiles.MediaDatabase, which resolves under the app path
// (NOMERCY_APP_PATH). Every query below is a read (AsNoTracking); nothing mutates.
file sealed class MediaContextFactory : IDbContextFactory<MediaContext>
{
    public MediaContext CreateDbContext() => new();
}

// Same real-database binding, but every executed SQL command is logged with its
// server-side elapsed time. Routing a repository through this factory (instead of
// the silent one) reveals which split-query leg dominates a slow call, without
// touching production code. Used by the --sql diagnostic only.
file sealed class LoggingMediaContextFactory : IDbContextFactory<MediaContext>
{
    public MediaContext CreateDbContext()
    {
        DbContextOptionsBuilder<MediaContext> builder = new();
        builder.UseSqlite(
            $"Data Source={AppFiles.MediaDatabase}; Pooling=True; Foreign Keys=True; Default Timeout=30;",
            o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
        );
        builder.LogTo(
            Console.WriteLine,
            [Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted]
        );
        return new(builder.Options);
    }

    public Task<MediaContext> CreateDbContextAsync(CancellationToken ct = default) =>
        Task.FromResult(CreateDbContext());
}

// A named query to time. Run returns the raw result so the harness can both count
// its rows and fingerprint its content (to prove an optimization loses no data).
internal sealed record BenchmarkCase(string Name, Func<Task<object?>> Run);

internal sealed record BenchmarkResult(
    string Name,
    double ColdMs,
    double WarmMedianMs,
    double WarmP95Ms,
    double WarmMinMs,
    double WarmMaxMs,
    int Rows,
    string Signature,
    string? Error
);

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Dictionary<string, string> opts = ParseArgs(args);

        int samples = int.TryParse(opts.GetValueOrDefault("samples"), out int s) ? s : 3;
        Guid userId = Guid.Parse(
            opts.GetValueOrDefault("user", "37d03e60-7b0a-4246-a85b-a5618966a383")
        );
        Ulid libraryId = Ulid.Parse(
            opts.GetValueOrDefault("library", "01HQ5W2HMZ5QKDSXTTN9EQRERH")
        );
        Guid artistId = Guid.Parse(
            opts.GetValueOrDefault("artist", "27638856-79A1-4495-B7DA-1912899560C7")
        );
        int tvId = int.Parse(opts.GetValueOrDefault("tv", "14610"));
        int movieId = int.Parse(opts.GetValueOrDefault("movie", "13654"));
        string language = opts.GetValueOrDefault("language", "en");
        string country = opts.GetValueOrDefault("country", "NL");
        string searchTerm = opts.GetValueOrDefault("query", "love");
        string? filter = opts.GetValueOrDefault("filter");

        // Bind to the real database. Default to the dev app root; override with
        // --app-path or NOMERCY_APP_PATH to benchmark a production data directory.
        string appPath =
            opts.GetValueOrDefault("app-path")
            ?? Environment.GetEnvironmentVariable("NOMERCY_APP_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoMercy_dev"
            );
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", appPath);

        string dbPath = AppFiles.MediaDatabase;
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"media.db not found at: {dbPath}");
            Console.Error.WriteLine(
                "Pass --app-path <NoMercy app data root> to point at the real DB."
            );
            return 1;
        }

        long dbSizeMb = new FileInfo(dbPath).Length / (1024 * 1024);
        Console.WriteLine("NoMercy query benchmarks (read-only, real database)");
        Console.WriteLine($"  db       : {dbPath} ({dbSizeMb} MB)");
        Console.WriteLine($"  user     : {userId}");
        Console.WriteLine($"  samples  : {samples} warm runs + 1 cold, fresh DbContext per run");
        Console.WriteLine(
            $"  built    : {(IsDebug() ? "DEBUG (run -c Release for reliable numbers)" : "Release")}"
        );
        Console.WriteLine();

        MediaContextFactory factory = new();

        // Load the mega-artist's per-track album color palettes once. The palette A/B
        // cases below iterate this in-memory list, so they time only the conversion
        // strategy — the old deserialize+reserialize round-trip vs the new ToRaw()
        // passthrough — and not the query that fetched the strings.
        List<string?> artistPalettes;
        await using (MediaContext paletteCtx = factory.CreateDbContext())
        {
            artistPalettes = await paletteCtx
                .AlbumTrack.AsNoTracking()
                .Where(albumTrack =>
                    albumTrack.Track.ArtistTrack.Any(artistTrack =>
                        artistTrack.ArtistId == artistId
                    )
                )
                .Select(albumTrack => albumTrack.Album._colorPalette)
                .ToListAsync();
        }
        Console.WriteLine(
            $"  palettes : {artistPalettes.Count} track-album color palettes loaded for the A/B"
        );
        Console.WriteLine();

        // --sql: run GetArtistAsync through a logging context so every split-query leg
        // prints its own "Executed DbCommand (Xms)". Run it twice; the second pass is
        // warm and shows where the wall-clock actually goes. Then exit.
        if (opts.ContainsKey("sql"))
        {
            LoggingMediaContextFactory loggingFactory = new();
            Console.WriteLine("=== GetArtistAsync SQL (cold) ===");
            await new MusicRepository(loggingFactory).GetArtistAsync(userId, artistId);
            Console.WriteLine();
            Console.WriteLine("=== GetArtistAsync SQL (warm) ===");
            Stopwatch sqlSw = Stopwatch.StartNew();
            await new MusicRepository(loggingFactory).GetArtistAsync(userId, artistId);
            sqlSw.Stop();
            Console.WriteLine();
            Console.WriteLine($"=== warm total: {sqlSw.ElapsedMilliseconds}ms ===");
            return 0;
        }

        List<BenchmarkCase> cases =
        [
            new(
                "genres        GetGenresWithCountsAsync",
                async () =>
                {
                    await using MediaContext c = factory.CreateDbContext();
                    return await new GenreRepository(c).GetGenresWithCountsAsync(
                        userId,
                        language,
                        21,
                        0
                    );
                }
            ),
            new(
                "home          GetHome",
                async () =>
                {
                    await using MediaContext c = factory.CreateDbContext();
                    return await new HomeRepository(c, factory).GetHome(userId, language, 21, 0);
                }
            ),
            new(
                "home          GetHomeParallelDataAsync",
                async () =>
                {
                    await using MediaContext c = factory.CreateDbContext();
                    return await new HomeRepository(c, factory).GetHomeParallelDataAsync(
                        userId,
                        language,
                        country
                    );
                }
            ),
            new(
                "home          GetHomeGenresAsync",
                async () =>
                {
                    await using MediaContext c = factory.CreateDbContext();
                    return await new HomeRepository(c, factory).GetHomeGenresAsync(
                        userId,
                        language,
                        21,
                        0
                    );
                }
            ),
            new(
                "screensaver   GetScreensaverImagesAsync",
                async () =>
                {
                    await using MediaContext c = factory.CreateDbContext();
                    return await new HomeRepository(c, factory).GetScreensaverImagesAsync(userId);
                }
            ),
            new(
                "libraries     GetRandomTvCardAsync",
                async () =>
                    await new LibraryRepository(factory).GetRandomTvCardAsync(
                        userId,
                        language,
                        country
                    )
            ),
            new(
                "libraries     GetRandomMovieCardAsync",
                async () =>
                    await new LibraryRepository(factory).GetRandomMovieCardAsync(
                        userId,
                        language,
                        country
                    )
            ),
            new(
                "libraries     GetLibraryMovieCardsAsync",
                async () =>
                    await new LibraryRepository(factory).GetLibraryMovieCardsAsync(
                        userId,
                        libraryId,
                        country,
                        10,
                        0
                    )
            ),
            new(
                "libraries/tv  GetLibraryTvCardsAsync",
                async () =>
                    await new LibraryRepository(factory).GetLibraryTvCardsAsync(
                        userId,
                        libraryId,
                        country,
                        50,
                        0
                    )
            ),
            new(
                "video/tv      GetTvAsync",
                async () =>
                    await new TvShowRepository(
                        factory,
                        new MediaTypeClassifier(
                            new AniListMetadataProvider(),
                            new JikanMetadataProvider()
                        )
                    ).GetTvAsync(userId, tvId, language, country)
            ),
            new(
                "video/movie   GetMovieAsync",
                async () =>
                    await new MovieRepository(
                        factory,
                        NullLogger<MovieRepository>.Instance
                    ).GetMovieAsync(userId, movieId, language, country)
            ),
            new(
                "music/artist  GetArtistAsync",
                async () => await new MusicRepository(factory).GetArtistAsync(userId, artistId)
            ),
            // Full server-side response cost for the artist endpoint: load + build the
            // exact ArtistResponseItemDto the controller returns + serialize it with the
            // API's Newtonsoft settings. This is everything except HTTP/auth/network, so
            // it is the honest "is the endpoint under the budget" number.
            new(
                "music/artist  full response build+serialize",
                async () =>
                {
                    Artist? artist = await new MusicRepository(factory).GetArtistAsync(
                        userId,
                        artistId
                    );
                    if (artist is null)
                        return 0;
                    Api.DTOs.Music.ArtistResponseItemDto dto = new(artist, userId, country);
                    Newtonsoft.Json.JsonSerializerSettings settings = new()
                    {
                        ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
                        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
                    };
                    return Newtonsoft.Json.JsonConvert.SerializeObject(dto, settings).Length;
                }
            ),
            // A/B for the color-palette response cost. Both serialize with Newtonsoft
            // (as the real response does); the only difference is how the stored JSON
            // string becomes a serializable value. Old: parse into a typed ColorPalette
            // then reflect it back to JSON. New: parse straight to a JToken and emit it
            // verbatim. Same output, one fewer reflection pass per palette.
            new(
                "palette       roundtrip Deserialize+Serialize (old)",
                () =>
                    Task.FromResult<object?>(
                        artistPalettes.Count(palette =>
                            Newtonsoft.Json.JsonConvert.SerializeObject(
                                ColorPalette.FromJsonOrNull(palette)
                            )
                                is not null
                        )
                    )
            ),
            new(
                "palette       ToRaw passthrough (new)",
                () =>
                    Task.FromResult<object?>(
                        artistPalettes.Count(palette =>
                            Newtonsoft.Json.JsonConvert.SerializeObject(palette.ToRaw()) is not null
                        )
                    )
            ),
            new(
                "search        SearchTrackIdsAsync",
                async () =>
                    await new MusicRepository(factory).SearchTrackIdsAsync(searchTerm, userId)
            ),
            new(
                "search        SearchArtistIdsAsync",
                async () =>
                    await new MusicRepository(factory).SearchArtistIdsAsync(searchTerm, userId)
            ),
            new(
                "search        SearchAlbumIdsAsync",
                async () =>
                    await new MusicRepository(factory).SearchAlbumIdsAsync(searchTerm, userId)
            ),
            new(
                "search        SearchPlaylistIdsAsync",
                async () =>
                    await new MusicRepository(factory).SearchPlaylistIdsAsync(searchTerm, userId)
            ),
            new(
                "search        SearchTrackCards(uncapped)",
                async () =>
                {
                    MusicRepository repo = new(factory);
                    List<Guid> t = await repo.SearchTrackIdsAsync(searchTerm, userId);
                    return await repo.SearchTrackCardsAsync(t, userId, country);
                }
            ),
            new(
                "search        SearchTrackCards(cap50)",
                async () =>
                {
                    MusicRepository repo = new(factory);
                    List<Guid> t =
                    [
                        .. (await repo.SearchTrackIdsAsync(searchTerm, userId)).Take(50),
                    ];
                    return await repo.SearchTrackCardsAsync(t, userId, country);
                }
            ),
            new(
                "search        SearchMusicFullDataAsync",
                async () =>
                {
                    MusicRepository repo = new(factory);
                    List<Guid> a = await repo.SearchArtistIdsAsync(searchTerm, userId);
                    List<Guid> al = await repo.SearchAlbumIdsAsync(searchTerm, userId);
                    List<Guid> p = await repo.SearchPlaylistIdsAsync(searchTerm, userId);
                    List<Guid> t = await repo.SearchTrackIdsAsync(searchTerm, userId);
                    return await repo.SearchMusicFullDataAsync(a, al, p, t);
                }
            ),
        ];

        if (filter is not null)
            cases =
            [
                .. cases.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)),
            ];

        List<BenchmarkResult> results = [];
        Console.WriteLine(
            $"{"query", -42} {"cold", 8} {"p50", 8} {"p95", 8} {"rows", 7}  {"signature", 16}"
        );
        Console.WriteLine(new string('-', 96));
        foreach (BenchmarkCase bench in cases)
        {
            BenchmarkResult res = await Measure(bench, samples);
            results.Add(res);
            if (res.Error is not null)
                Console.WriteLine($"{res.Name, -42} ERROR: {res.Error}");
            else
                Console.WriteLine(
                    $"{res.Name, -42} {res.ColdMs, 6:F0}ms {res.WarmMedianMs, 6:F0}ms {res.WarmP95Ms, 6:F0}ms {res.Rows, 7}  {res.Signature, 16}"
                );
        }

        Console.WriteLine(new string('-', 96));
        Console.WriteLine("Ranked by warm p50 (slowest first):");
        foreach (
            BenchmarkResult r in results
                .Where(r => r.Error is null)
                .OrderByDescending(r => r.WarmMedianMs)
        )
            Console.WriteLine($"  {r.WarmMedianMs, 8:F0}ms  {r.Name.Trim()}");

        // Data-integrity guard: compare each query's content fingerprint against a
        // prior run. A changed signature means the result SET changed (rows missing,
        // added, or altered) — the exact failure mode an "optimization" must not cause.
        string? baseline = opts.GetValueOrDefault("baseline");
        if (baseline is not null && File.Exists(baseline))
        {
            List<BenchmarkResult>? prev = JsonSerializer.Deserialize<List<BenchmarkResult>>(
                await File.ReadAllTextAsync(baseline)
            );
            bool anyChanged = false;
            Console.WriteLine($"\nData-integrity check vs baseline ({baseline}):");
            foreach (BenchmarkResult r in results.Where(r => r.Error is null))
            {
                BenchmarkResult? b = prev?.FirstOrDefault(p => p.Name == r.Name);
                if (b is null)
                    Console.WriteLine($"  NEW      {r.Name.Trim()} ({r.Rows} rows)");
                else if (b.Signature != r.Signature)
                {
                    anyChanged = true;
                    Console.WriteLine(
                        $"  CHANGED  {r.Name.Trim()}  rows {b.Rows} -> {r.Rows}  sig {b.Signature} -> {r.Signature}"
                    );
                }
                else
                    Console.WriteLine($"  OK       {r.Name.Trim()} (unchanged, {r.Rows} rows)");
            }

            if (anyChanged)
                Console.WriteLine(
                    "\nWARNING: a query's result set changed vs baseline — verify no data went missing."
                );
        }

        string outPath = opts.GetValueOrDefault("json", "benchmark-results.json");
        await File.WriteAllTextAsync(
            outPath,
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })
        );
        Console.WriteLine($"\nWrote {outPath} (use it as --baseline on the next run)");
        return 0;
    }

    private static async Task<BenchmarkResult> Measure(BenchmarkCase bench, int samples)
    {
        try
        {
            object? last;
            Stopwatch sw = Stopwatch.StartNew();
            last = await bench.Run(); // cold: query-plan compile + cold page cache
            sw.Stop();
            double cold = sw.Elapsed.TotalMilliseconds;

            List<double> warm = new(samples);
            for (int i = 0; i < samples; i++)
            {
                sw.Restart();
                last = await bench.Run();
                sw.Stop();
                warm.Add(sw.Elapsed.TotalMilliseconds);
            }

            warm.Sort();
            (int rows, string signature) = Fingerprint(last); // untimed
            return new(
                bench.Name.Trim(),
                cold,
                Median(warm),
                Percentile(warm, 95),
                warm[0],
                warm[^1],
                rows,
                signature,
                null
            );
        }
        catch (Exception ex)
        {
            return new(
                bench.Name.Trim(),
                0,
                0,
                0,
                0,
                0,
                0,
                "-",
                $"{ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    // Order-independent content fingerprint of a query result. For a collection,
    // each item is serialized and hashed, the hashes are sorted, then combined — so
    // the signature is invariant to row order but sensitive to any missing, added,
    // or changed row. Cycles in EF entities are dropped rather than throwing.
    private static (int Count, string Signature) Fingerprint(object? result)
    {
        if (result is null)
            return (0, "-");

        JsonSerializerOptions opts = new()
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            MaxDepth = 64,
        };

        if (result is IEnumerable enumerable and not string)
        {
            List<string> hashes = [];
            foreach (object? item in enumerable)
                hashes.Add(SafeHash(item, opts));
            hashes.Sort(StringComparer.Ordinal);
            return (hashes.Count, Sha(string.Join('\n', hashes))[..16]);
        }

        return (1, SafeHash(result, opts)[..16]);
    }

    private static string SafeHash(object? item, JsonSerializerOptions opts)
    {
        try
        {
            return Sha(JsonSerializer.Serialize(item, opts));
        }
        catch
        {
            return Sha(item?.ToString() ?? "null");
        }
    }

    private static string Sha(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static double Median(List<double> sorted)
    {
        int n = sorted.Count;
        if (n == 0)
            return 0;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static double Percentile(List<double> sorted, int p)
    {
        if (sorted.Count == 0)
            return 0;
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        Dictionary<string, string> opts = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                continue;
            string key = a[2..];
            int eq = key.IndexOf('=');
            if (eq >= 0)
            {
                opts[key[..eq]] = key[(eq + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                opts[key] = args[++i];
            }
            else
            {
                opts[key] = "true";
            }
        }

        return opts;
    }

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
