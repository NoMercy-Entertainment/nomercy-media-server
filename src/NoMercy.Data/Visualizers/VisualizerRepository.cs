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
using NoMercy.Database.Models.Media;

namespace NoMercy.Data.Visualizers;

public class VisualizerRepository(MediaContext context) : IVisualizerRepository
{
    public const int MaxFavorites = 500;
    public const int MaxRecents = 10;

    public async Task<VisualizerPage> ListAsync(VisualizerListQuery query)
    {
        Dictionary<Ulid, VisualizerUser> links = (
            await context
                .VisualizerUsers.AsNoTracking()
                .Where(link => link.UserId == query.UserId)
                .ToListAsync()
        ).ToDictionary(link => link.VisualizerId);

        IQueryable<Visualizer> rows = context.Visualizers.AsNoTracking();
        if (!query.IncludeDisabled)
            rows = rows.Where(v => v.Enabled);
        if (query.Formats.Count > 0)
            rows = rows.Where(v => query.Formats.Contains(v.Format));
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            string tag = "," + query.Tag.Trim().ToLowerInvariant() + ",";
            rows = rows.Where(v => ("," + v.Tags + ",").ToLower().Contains(tag));
        }
        if (!string.IsNullOrWhiteSpace(query.Author))
        {
            string author = query.Author.Trim().ToLowerInvariant();
            rows = rows.Where(v => v.Author.ToLower() == author);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string text = query.Search.Trim().ToLowerInvariant();
            rows = rows.Where(v =>
                v.Name.ToLower().Contains(text)
                || v.Author.ToLower().Contains(text)
                || v.Tags.ToLower().Contains(text)
            );
        }

        bool recent = query.Sort == VisualizerListQuery.SortRecent;
        if (query.FavoritesOnly || recent)
        {
            List<Ulid> wanted = links
                .Values.Where(link => recent ? link.LastUsedAt is not null : link.IsFavorite)
                .Select(link => link.VisualizerId)
                .ToList();
            rows = rows.Where(v => wanted.Contains(v.Id));
        }

        IQueryable<Visualizer> summaries = rows.Select(v => new Visualizer
        {
            Id = v.Id,
            Slug = v.Slug,
            Name = v.Name,
            Author = v.Author,
            License = v.License,
            SourceUrl = v.SourceUrl,
            SourceSha1 = v.SourceSha1,
            Format = v.Format,
            Tags = v.Tags,
            Textures = v.Textures,
            Schema = v.Schema,
            ContentHash = v.ContentHash,
            PayloadBytes = v.PayloadBytes,
            RenderableWebgl2 = v.RenderableWebgl2,
            IsBuiltIn = v.IsBuiltIn,
            Source = v.Source,
            Enabled = v.Enabled,
            CreatedAt = v.CreatedAt,
            UpdatedAt = v.UpdatedAt,
            PayloadJson = "",
        });

        List<Visualizer> page;
        if (recent)
        {
            List<Visualizer> all = await summaries.ToListAsync();
            page = all.OrderByDescending(v => links[v.Id].LastUsedAt)
                .Skip(query.Page * query.Take)
                .Take(query.Take + 1)
                .ToList();
        }
        else
        {
            IOrderedQueryable<Visualizer> ordered =
                query.Sort == VisualizerListQuery.SortAuthor
                    ? summaries.OrderBy(v => v.Author.ToLower())
                    : summaries.OrderBy(v => v.Name.ToLower());
            page = await ordered
                .ThenBy(v => v.Id)
                .Skip(query.Page * query.Take)
                .Take(query.Take + 1)
                .ToListAsync();
        }

        bool hasMore = page.Count > query.Take;
        return new(page.Take(query.Take).ToList(), links, hasMore);
    }

    public async Task<Visualizer?> GetAsync(Ulid id, bool includeDisabled)
    {
        return await context
            .Visualizers.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == id && (includeDisabled || v.Enabled));
    }

    /// <summary>The pinned hash while nothing local differs; user entries and disabled built-ins fold in.</summary>
    public async Task<VisualizerBundleVersion> GetBundleVersionAsync()
    {
        List<Visualizer> rows = await context
            .Visualizers.AsNoTracking()
            .Select(v => new Visualizer
            {
                Id = v.Id,
                Slug = v.Slug,
                Name = v.Name,
                Author = v.Author,
                License = v.License,
                SourceUrl = v.SourceUrl,
                Format = v.Format,
                Tags = v.Tags,
                Textures = v.Textures,
                ContentHash = v.ContentHash,
                IsBuiltIn = v.IsBuiltIn,
                Enabled = v.Enabled,
                UpdatedAt = v.UpdatedAt,
                PayloadJson = "",
            })
            .ToListAsync();

        List<string> local = rows.Where(v => !v.IsBuiltIn)
            .Select(v =>
                string.Join(
                    '|',
                    v.Id,
                    v.Slug,
                    v.Name,
                    v.Author,
                    v.License,
                    v.SourceUrl,
                    v.Tags,
                    v.Textures,
                    v.ContentHash,
                    v.Enabled
                )
            )
            .Concat(rows.Where(v => v.IsBuiltIn && !v.Enabled).Select(v => $"{v.Id}|off"))
            .Order(StringComparer.Ordinal)
            .ToList();

        string pinned = VisualizerBundle.PinnedHash();
        string hash =
            local.Count == 0
                ? pinned
                : VisualizerCanonicalJson.Hash(pinned + "\n" + string.Join('\n', local));

        return new(
            hash,
            rows.Count(v => v.Enabled),
            rows.Count == 0 ? null : rows.Max(v => v.UpdatedAt)
        );
    }

    public async Task<Visualizer> CreateAsync(Visualizer visualizer)
    {
        while (await context.Visualizers.AnyAsync(v => v.Slug == visualizer.Slug))
            visualizer.Slug = VisualizerSlug.Create(visualizer.Format, visualizer.Name);

        context.Visualizers.Add(visualizer);
        await context.SaveChangesAsync();
        return visualizer;
    }

    public async Task<Visualizer?> UpdateAsync(Ulid id, Action<Visualizer> update)
    {
        Visualizer? visualizer = await context.Visualizers.FirstOrDefaultAsync(v => v.Id == id);
        if (visualizer is null)
            return null;

        update(visualizer);
        await context.SaveChangesAsync();
        return visualizer;
    }

    public async Task<bool> DeleteAsync(Ulid id)
    {
        Visualizer? visualizer = await context.Visualizers.FirstOrDefaultAsync(v => v.Id == id);
        if (visualizer is null)
            return false;

        List<VisualizerUser> links = await context
            .VisualizerUsers.Where(link => link.VisualizerId == id)
            .ToListAsync();
        context.VisualizerUsers.RemoveRange(links);
        context.Visualizers.Remove(visualizer);
        await context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetEnabledAsync(Ulid id, bool enabled)
    {
        Visualizer? visualizer = await context.Visualizers.FirstOrDefaultAsync(v => v.Id == id);
        if (visualizer is null)
            return false;

        visualizer.Enabled = enabled;
        await context.SaveChangesAsync();
        return true;
    }

    public async Task<VisualizerWriteResult> SetFavoriteAsync(
        Guid userId,
        Ulid id,
        bool favorite,
        bool includeDisabled
    )
    {
        if (!await context.Visualizers.AnyAsync(v => v.Id == id && (includeDisabled || v.Enabled)))
            return VisualizerWriteResult.NotFound;

        VisualizerUser? link = await context.VisualizerUsers.FirstOrDefaultAsync(l =>
            l.VisualizerId == id && l.UserId == userId
        );

        if (favorite && link?.IsFavorite != true)
        {
            int count = await context.VisualizerUsers.CountAsync(l =>
                l.UserId == userId && l.IsFavorite
            );
            if (count >= MaxFavorites)
                return VisualizerWriteResult.LimitReached;
        }

        if (link is null)
        {
            if (!favorite)
                return VisualizerWriteResult.Ok;
            link = new() { VisualizerId = id, UserId = userId };
            context.VisualizerUsers.Add(link);
        }

        link.IsFavorite = favorite;
        await NormalizeAsync(userId);
        return VisualizerWriteResult.Ok;
    }

    public async Task<VisualizerChoiceState> GetChoiceAsync(Guid userId)
    {
        VisualizerUser? chosen = await context
            .VisualizerUsers.AsNoTracking()
            .FirstOrDefaultAsync(l => l.UserId == userId && l.ChosenAt != null);
        if (chosen is null)
            return new(null, null);

        bool enabled = await context.Visualizers.AnyAsync(v =>
            v.Id == chosen.VisualizerId && v.Enabled
        );
        return new(enabled ? chosen.VisualizerId : null, chosen.ChosenAt);
    }

    public async Task<VisualizerWriteResult> SetChoiceAsync(Guid userId, Ulid? id)
    {
        if (
            id is { } wanted
            && !await context.Visualizers.AnyAsync(v => v.Id == wanted && v.Enabled)
        )
            return VisualizerWriteResult.NotFound;

        await ApplyChoiceAsync(userId, id, DateTime.UtcNow);
        await NormalizeAsync(userId);
        return VisualizerWriteResult.Ok;
    }

    public async Task<VisualizerImportResult> ImportDeviceStateAsync(
        Guid userId,
        string? choice,
        IReadOnlyList<string> favorites,
        IReadOnlyList<string> recents
    )
    {
        List<VisualizerImportSkip> skipped = [];
        Dictionary<string, Visualizer> bySlug = await ResolveAsync([
            .. favorites,
            .. recents,
            .. choice is null ? [] : new[] { choice },
        ]);
        List<VisualizerUser> links = await context
            .VisualizerUsers.Where(l => l.UserId == userId)
            .ToListAsync();
        DateTime now = DateTime.UtcNow;

        VisualizerUser Link(Ulid id)
        {
            VisualizerUser? link = links.FirstOrDefault(l => l.VisualizerId == id);
            if (link is not null)
                return link;
            link = new() { VisualizerId = id, UserId = userId };
            links.Add(link);
            context.VisualizerUsers.Add(link);
            return link;
        }

        bool choiceApplied = false;
        if (choice is not null && links.All(l => l.ChosenAt is null))
        {
            if (Resolve(choice, bySlug, skipped) is { } chosen)
            {
                Link(chosen.Id).ChosenAt = now;
                choiceApplied = true;
            }
        }

        int favoritesAdded = 0;
        int favoriteCount = links.Count(l => l.IsFavorite);
        foreach (string value in favorites)
        {
            if (Resolve(value, bySlug, skipped) is not { } entry)
                continue;
            VisualizerUser link = Link(entry.Id);
            if (link.IsFavorite || favoriteCount >= MaxFavorites)
            {
                skipped.Add(new(value, "duplicate"));
                continue;
            }
            link.IsFavorite = true;
            favoriteCount++;
            favoritesAdded++;
        }

        int recentsAdded = 0;
        for (int i = 0; i < recents.Count; i++)
        {
            if (Resolve(recents[i], bySlug, skipped) is not { } entry)
                continue;
            VisualizerUser link = Link(entry.Id);
            DateTime stamp = now.AddSeconds(-(i + 1));
            if (link.LastUsedAt is not null)
            {
                skipped.Add(new(recents[i], "duplicate"));
                continue;
            }
            link.LastUsedAt = stamp;
            recentsAdded++;
        }

        await NormalizeAsync(userId);
        return new(choiceApplied, favoritesAdded, recentsAdded, skipped);
    }

    private static Visualizer? Resolve(
        string value,
        Dictionary<string, Visualizer> bySlug,
        List<VisualizerImportSkip> skipped
    )
    {
        if (value == "bars")
        {
            skipped.Add(new(value, "bars"));
            return null;
        }
        if (!bySlug.TryGetValue(ToSlug(value), out Visualizer? entry))
        {
            skipped.Add(new(value, "unknown"));
            return null;
        }
        if (!entry.Enabled)
        {
            skipped.Add(new(value, "disabled"));
            return null;
        }
        return entry;
    }

    /// <summary>Device forms: "milkdrop:id" and a bare shader id; library slugs map to themselves.</summary>
    private static string ToSlug(string value)
    {
        if (value.StartsWith("milkdrop:", StringComparison.Ordinal))
            return "milkdrop/" + value["milkdrop:".Length..];
        if (
            value.StartsWith("milkdrop/", StringComparison.Ordinal)
            || value.StartsWith("shader/", StringComparison.Ordinal)
        )
            return value;
        return "shader/" + value;
    }

    private async Task<Dictionary<string, Visualizer>> ResolveAsync(IEnumerable<string> values)
    {
        List<string> slugs = values.Select(ToSlug).Distinct().ToList();
        List<Visualizer> rows = await context
            .Visualizers.AsNoTracking()
            .Where(v => slugs.Contains(v.Slug))
            .Select(v => new Visualizer
            {
                Id = v.Id,
                Slug = v.Slug,
                Name = "",
                Author = "",
                License = "",
                Format = "",
                ContentHash = "",
                Enabled = v.Enabled,
                PayloadJson = "",
            })
            .ToListAsync();
        return rows.ToDictionary(v => v.Slug);
    }

    private async Task ApplyChoiceAsync(Guid userId, Ulid? id, DateTime now)
    {
        List<VisualizerUser> links = await context
            .VisualizerUsers.Where(l => l.UserId == userId)
            .ToListAsync();
        foreach (VisualizerUser link in links)
            link.ChosenAt = null;

        if (id is not { } chosen)
            return;

        VisualizerUser? target = links.FirstOrDefault(l => l.VisualizerId == chosen);
        if (target is null)
        {
            target = new() { VisualizerId = chosen, UserId = userId };
            context.VisualizerUsers.Add(target);
        }
        target.ChosenAt = now;
        target.LastUsedAt = now;
    }

    /// <summary>Keeps the newest recents, drops links that hold nothing, and saves.</summary>
    private async Task NormalizeAsync(Guid userId)
    {
        await context.SaveChangesAsync();
        List<VisualizerUser> links = await context
            .VisualizerUsers.Where(l => l.UserId == userId)
            .ToListAsync();

        foreach (
            VisualizerUser old in links
                .Where(l => l.LastUsedAt is not null)
                .OrderByDescending(l => l.LastUsedAt)
                .Skip(MaxRecents)
        )
            old.LastUsedAt = null;

        context.VisualizerUsers.RemoveRange(
            links.Where(l => !l.IsFavorite && l.LastUsedAt is null && l.ChosenAt is null)
        );
        await context.SaveChangesAsync();
    }
}
