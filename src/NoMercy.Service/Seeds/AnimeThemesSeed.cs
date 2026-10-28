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
using NoMercy.Database.Models.Common;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;
using Serilog.Events;
using DbConfiguration = NoMercy.Database.Models.Common.Configuration;

namespace NoMercy.Service.Seeds;

public static class AnimeThemesSeed
{
    internal const string FetchedAtKey =
        ConfigurationBookkeeping.SeedStatePrefix + "anime_themes_fetched_at";
    internal const string FailedAtKey =
        ConfigurationBookkeeping.SeedStatePrefix + "anime_themes_failed_at";

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    public static Task Init(this MediaContext dbContext)
    {
        return Init(dbContext, new AniListMetadataProvider(), new JikanMetadataProvider());
    }

    public static async Task Init(
        MediaContext dbContext,
        IAniListMetadataProvider aniList,
        IJikanMetadataProvider jikan,
        AppDbContext? appDbContext = null
    )
    {
        bool ownsAppDb = appDbContext is null;
        appDbContext ??= new();
        try
        {
            await Seed(dbContext, aniList, jikan, appDbContext);
        }
        finally
        {
            if (ownsAppDb)
                await appDbContext.DisposeAsync();
        }
    }

    // Seeds per name rather than skipping when any row exists: enrichment creates
    // theme rows as titles are imported, so a populated table says nothing about
    // whether the full list is there. Names compare trimmed and case-insensitive,
    // like the enrichment resolvers, so a seeded row is found by the link step.
    private static async Task Seed(
        MediaContext dbContext,
        IAniListMetadataProvider aniList,
        IJikanMetadataProvider jikan,
        AppDbContext appDbContext
    )
    {
        if (await IsWithin(appDbContext, FetchedAtKey, RefreshAfter))
            return;
        if (await IsWithin(appDbContext, FailedAtKey, RetryAfter))
            return;

        Dictionary<string, string?> themes = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> demographics = new(StringComparer.OrdinalIgnoreCase);
        bool complete = true;

        try
        {
            AniListMediaTag[]? tags = await aniList.GetTagCollectionAsync();
            complete &= tags is not null;
            foreach (AniListMediaTag tag in (tags ?? []).Where(AniListTagFilter.IsTheme))
                Collect(themes, tag.Name, tag.Description);
        }
        catch (Exception e)
        {
            complete = false;
            WarnUnreachable("AniList", e);
        }

        try
        {
            JikanGenre[]? jikanThemes = await jikan.GetGenresAsync("themes");
            complete &= jikanThemes is not null;
            foreach (JikanGenre genre in jikanThemes ?? [])
                Collect(themes, genre.Name, null);

            JikanGenre[]? jikanDemographics = await jikan.GetGenresAsync("demographics");
            complete &= jikanDemographics is not null;
            foreach (JikanGenre genre in jikanDemographics ?? [])
                Collect(demographics, genre.Name, null);
        }
        catch (Exception e)
        {
            complete = false;
            WarnUnreachable("Jikan", e);
        }

        if (themes.Count > 0 || demographics.Count > 0)
        {
            try
            {
                await SeedThemesAsync(dbContext, themes);
                await SeedDemographicsAsync(dbContext, demographics);
            }
            catch (Exception e)
            {
                complete = false;
                Logger.Setup($"Anime themes seed failed: {e.Message}", LogEventLevel.Warning);
            }
        }

        await RecordAttempt(appDbContext, complete);
    }

    private static void WarnUnreachable(string source, Exception e)
    {
        Logger.Setup(
            $"Anime themes seed: {source} is not reachable right now. The themes already stored stay as they are, and the seed tries again in a few hours.",
            LogEventLevel.Warning
        );
        Logger.Setup($"Anime themes seed: {source}: {e.Message}", LogEventLevel.Verbose);
    }

    private static async Task<bool> IsWithin(AppDbContext appDbContext, string key, TimeSpan window)
    {
        string? stored = await appDbContext
            .Configuration.AsNoTracking()
            .Where(c => c.Key == key)
            .Select(c => c.Value)
            .FirstOrDefaultAsync();
        if (
            !DateTime.TryParse(
                stored,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTime when
            )
        )
            return false;

        TimeSpan age = DateTime.UtcNow - when.ToUniversalTime();
        return age >= TimeSpan.Zero && age < window;
    }

    private static async Task RecordAttempt(AppDbContext appDbContext, bool complete)
    {
        string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        string key = complete ? FetchedAtKey : FailedAtKey;

        DbConfiguration? row = await appDbContext.Configuration.FirstOrDefaultAsync(c =>
            c.Key == key
        );
        if (row is null)
            appDbContext.Configuration.Add(new() { Key = key, Value = now });
        else
            row.Value = now;

        if (complete)
            await appDbContext.Configuration.Where(c => c.Key == FailedAtKey).ExecuteDeleteAsync();

        await appDbContext.SaveChangesAsync();
    }

    private static void Collect(
        Dictionary<string, string?> target,
        string name,
        string? description
    )
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            return;

        string? text = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (!target.TryGetValue(trimmed, out string? known) || string.IsNullOrEmpty(known))
            target[trimmed] = text ?? known;
    }

    private static async Task SeedThemesAsync(
        MediaContext dbContext,
        Dictionary<string, string?> wanted
    )
    {
        List<AnimeTheme> existing = await dbContext.AnimeThemes.OrderBy(t => t.Id).ToListAsync();
        Dictionary<string, AnimeTheme> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (AnimeTheme theme in existing)
            byName.TryAdd(theme.Name.Trim(), theme);

        foreach ((string name, string? description) in wanted)
        {
            if (!byName.TryGetValue(name, out AnimeTheme? theme))
            {
                dbContext.AnimeThemes.Add(new() { Name = name, Description = description });
                continue;
            }

            if (string.IsNullOrWhiteSpace(theme.Description) && description is not null)
                theme.Description = description;
        }

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedDemographicsAsync(
        MediaContext dbContext,
        Dictionary<string, string?> wanted
    )
    {
        HashSet<string> existing = (
            await dbContext.AnimeDemographics.Select(d => d.Name).ToListAsync()
        )
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string name in wanted.Keys.Where(name => !existing.Contains(name)))
            dbContext.AnimeDemographics.Add(new() { Name = name });

        await dbContext.SaveChangesAsync();
    }
}
