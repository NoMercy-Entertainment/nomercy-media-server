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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.NmSystem;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.TMDB.Client;
using Serilog.Events;

namespace NoMercy.Service.Seeds;

public static class GenresSeed
{
    private const string MissKeyPrefix = "genre_translation_miss:";

    public static async Task Init(this MediaContext dbContext, AppDbContext? appDbContext = null)
    {
        bool ownsAppDb = appDbContext is null;
        appDbContext ??= new();
        try
        {
            await Seed(dbContext, appDbContext);
        }
        finally
        {
            if (ownsAppDb)
                await appDbContext.DisposeAsync();
        }
    }

    private static async Task Seed(MediaContext dbContext, AppDbContext appDbContext)
    {
        bool hasGenres = await dbContext.Genres.AnyAsync();
        List<Language> languages = await dbContext
            .Languages.AsNoTracking()
            .Where(language => language.Iso6391 != "en")
            .ToListAsync();
        List<int> genreIds = await dbContext
            .Genres.AsNoTracking()
            .Select(genre => genre.Id)
            .ToListAsync();
        List<Translation> existingTranslations = await dbContext
            .Translations.AsNoTracking()
            .Where(translation => translation.GenreId != null)
            .Select(translation => new Translation
            {
                GenreId = translation.GenreId,
                Iso6391 = translation.Iso6391,
            })
            .ToListAsync();
        HashSet<string> knownMisses = (
            await appDbContext
                .Configuration.AsNoTracking()
                .Where(c => c.Key.StartsWith(MissKeyPrefix))
                .Select(c => c.Key)
                .ToListAsync()
        ).ToHashSet();
        List<Language> missingLanguages = hasGenres
            ? languages
                .Where(language => !knownMisses.Contains(MissKeyPrefix + language.Iso6391))
                .Where(language =>
                    genreIds.Any(genreId =>
                        !existingTranslations.Any(translation =>
                            translation.GenreId == genreId
                            && translation.Iso6391 == language.Iso6391
                        )
                    )
                )
                .ToList()
            : languages;
        if (hasGenres && missingLanguages.Count == 0)
            return;

        Logger.Setup("Adding Genres", LogEventLevel.Verbose);

        TmdbMovieClient tmdbMovieClient = new();
        TmdbTvClient tmdbTvClient = new();

        if (!hasGenres)
        {
            try
            {
                List<Genre> genres = [];
                List<Genre>? movieGenres = (await tmdbMovieClient.Genres())
                    ?.Genres.Select(genre => new Genre
                    {
                        Id = genre.Id,
                        Name = genre.Name.OrEmpty(),
                    })
                    .ToList();
                genres.AddRange(movieGenres ?? []);

                List<Genre>? tvGenres = (await tmdbTvClient.Genres())
                    ?.Genres.Select(genre => new Genre
                    {
                        Id = genre.Id,
                        Name = genre.Name.OrEmpty(),
                    })
                    .ToList();
                genres.AddRange(tvGenres ?? []);

                await dbContext
                    .Genres.UpsertRange(genres)
                    .On(v => new { v.Id })
                    .WhenMatched(v => new() { Id = v.Id, Name = v.Name })
                    .RunAsync();
            }
            catch (Exception e)
            {
                Logger.Setup($"Genres seed failed: {e.Message}", LogEventLevel.Warning);
            }
        }

        if (missingLanguages.Count > 0)
        {
            try
            {
                ConcurrentBag<Translation> translations = [];
                ConcurrentBag<string> misses = [];

                await Parallel.ForEachAsync(
                    missingLanguages,
                    SystemParallelism.Options,
                    async (language, _) =>
                    {
                        Logger.Setup(
                            $"Adding Genres for {language.EnglishName}",
                            LogEventLevel.Verbose
                        );

                        int added = 0;
                        bool bothAnswered = true;
                        IEnumerable<Translation>? mg = (
                            await tmdbMovieClient.Genres(language.Iso6391)
                        )
                            ?.Genres.Where(g => g.Name != null)
                            .Select(genre => new Translation
                            {
                                GenreId = genre.Id,
                                Name = genre.Name.OrEmpty(),
                                Iso6391 = language.Iso6391,
                            });

                        if (mg != null)
                        {
                            foreach (Translation translation in mg)
                            {
                                translations.Add(translation);
                                added++;
                            }
                        }
                        else
                            bothAnswered = false;

                        IEnumerable<Translation>? tg = (await tmdbTvClient.Genres(language.Iso6391))
                            ?.Genres.Where(g => g.Name != null)
                            .Select(genre => new Translation
                            {
                                GenreId = genre.Id,
                                Name = genre.Name.OrEmpty(),
                                Iso6391 = language.Iso6391,
                            });

                        if (tg != null)
                        {
                            foreach (Translation translation in tg)
                            {
                                translations.Add(translation);
                                added++;
                            }
                        }
                        else
                            bothAnswered = false;

                        // TMDB answered both lists and has no translation for this language:
                        // remember the miss so the next boot does not ask again.
                        if (bothAnswered && added == 0)
                            misses.Add(language.Iso6391);
                    }
                );

                await RememberMisses(appDbContext, misses);

                Logger.Setup(
                    $"Adding {translations.Count} genre translations",
                    LogEventLevel.Verbose
                );

                await dbContext
                    .Translations.UpsertRange(translations.Where(genre => genre.Name != null))
                    .On(v => new { v.GenreId, v.Iso6391 })
                    .WhenMatched(v =>
                        new()
                        {
                            GenreId = v.GenreId,
                            Name = v.Name,
                            Iso6391 = v.Iso6391,
                        }
                    )
                    .RunAsync();
            }
            catch (Exception e)
            {
                Logger.Setup($"Genres seed failed: {e.Message}", LogEventLevel.Warning);
            }
        }
    }

    private static async Task RememberMisses(AppDbContext dbContext, IEnumerable<string> iso)
    {
        foreach (string code in iso.Distinct())
        {
            string key = MissKeyPrefix + code;
            if (await dbContext.Configuration.AnyAsync(c => c.Key == key))
                continue;
            dbContext.Configuration.Add(new() { Key = key, Value = "1" });
        }

        await dbContext.SaveChangesAsync();
    }
}
