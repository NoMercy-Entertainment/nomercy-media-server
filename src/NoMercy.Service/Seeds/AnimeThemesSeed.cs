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
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;
using Serilog.Events;

namespace NoMercy.Service.Seeds;

public static class AnimeThemesSeed
{
    public static Task Init(this MediaContext dbContext)
    {
        return Init(dbContext, new AniListMetadataProvider(), new JikanMetadataProvider());
    }

    // Seeds per name rather than skipping when any row exists: enrichment creates
    // theme rows as titles are imported, so a populated table says nothing about
    // whether the full list is there. Names compare trimmed and case-insensitive,
    // like the enrichment resolvers, so a seeded row is found by the link step.
    public static async Task Init(
        MediaContext dbContext,
        IAniListMetadataProvider aniList,
        IJikanMetadataProvider jikan
    )
    {
        Dictionary<string, string?> themes = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> demographics = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            AniListMediaTag[]? tags = await aniList.GetTagCollectionAsync();
            foreach (AniListMediaTag tag in (tags ?? []).Where(AniListTagFilter.IsTheme))
                Collect(themes, tag.Name, tag.Description);
        }
        catch (Exception e)
        {
            Logger.Setup(
                $"Anime themes seed: AniList tags failed: {e.Message}",
                LogEventLevel.Warning
            );
        }

        try
        {
            JikanGenre[]? jikanThemes = await jikan.GetGenresAsync("themes");
            foreach (JikanGenre genre in jikanThemes ?? [])
                Collect(themes, genre.Name, null);

            JikanGenre[]? jikanDemographics = await jikan.GetGenresAsync("demographics");
            foreach (JikanGenre genre in jikanDemographics ?? [])
                Collect(demographics, genre.Name, null);
        }
        catch (Exception e)
        {
            Logger.Setup(
                $"Anime themes seed: Jikan genres failed: {e.Message}",
                LogEventLevel.Warning
            );
        }

        if (themes.Count == 0 && demographics.Count == 0)
            return;

        try
        {
            await SeedThemesAsync(dbContext, themes);
            await SeedDemographicsAsync(dbContext, demographics);
        }
        catch (Exception e)
        {
            Logger.Setup($"Anime themes seed failed: {e.Message}", LogEventLevel.Warning);
        }
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
