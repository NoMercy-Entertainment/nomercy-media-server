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

/// <summary>
/// Upserts the built-in visualizers from the pinned bundle on every start.
/// Never touches user entries and never resets <see cref="Visualizer.Enabled"/>.
/// </summary>
public class VisualizerBundleSeeder(MediaContext context)
{
    public const string BuiltInSource = "builtin";
    public const string RetiredSource = "retired-builtin";

    public Task SeedAsync(CancellationToken ct = default) =>
        SeedAsync(VisualizerBundle.LoadEmbedded(), ct);

    public async Task SeedAsync(VisualizerBundle bundle, CancellationToken ct = default)
    {
        List<Visualizer> existing = await context
            .Visualizers.Where(v => v.IsBuiltIn || v.Source == RetiredSource)
            .ToListAsync(ct);
        Dictionary<Ulid, Visualizer> byId = existing.ToDictionary(v => v.Id);
        HashSet<string> takenSlugs = (
            await context.Visualizers.Select(v => v.Slug).ToListAsync(ct)
        ).ToHashSet(StringComparer.Ordinal);

        HashSet<Ulid> shipped = [];
        foreach (VisualizerBundleEntry entry in bundle.Entries)
        {
            Ulid id = VisualizerSlug.ToId(entry.Slug);
            shipped.Add(id);

            if (byId.TryGetValue(id, out Visualizer? row))
            {
                Apply(row, entry);
            }
            else if (takenSlugs.Contains(entry.Slug))
            {
                // A user entry already holds this slug; the unique index would refuse the insert.
                continue;
            }
            else
            {
                Visualizer created = new()
                {
                    Id = id,
                    Slug = entry.Slug,
                    Name = entry.Name,
                    Author = entry.Author,
                    License = entry.License,
                    Format = entry.Format,
                    ContentHash = entry.ContentHash,
                    PayloadJson = "",
                };
                Apply(created, entry);
                context.Visualizers.Add(created);
            }
        }

        await context.SaveChangesAsync(ct);
        await RetireUnshippedAsync(existing, shipped, ct);
    }

    private static void Apply(Visualizer row, VisualizerBundleEntry entry)
    {
        string payloadJson = VisualizerCanonicalJson.Serialize(entry.Payload);

        row.Slug = entry.Slug;
        row.Name = entry.Name;
        row.Author = entry.Author;
        row.License = entry.License;
        row.SourceUrl = entry.SourceUrl;
        row.SourceSha1 = entry.SourceSha1;
        row.Format = entry.Format;
        row.Tags = string.Join(',', entry.Tags);
        row.Textures = string.Join(',', entry.Textures);
        row.Schema = entry.Schema;
        row.ContentHash = entry.ContentHash;
        row.PayloadBytes = entry.PayloadBytes;
        row.RenderableWebgl2 = entry.Renderable.Webgl2;
        row.IsBuiltIn = true;
        row.Source = BuiltInSource;
        row.PayloadJson = payloadJson;
    }

    /// <summary>
    /// A built-in that no longer ships stays as a user entry when someone linked it
    /// (favorite, recent or current choice); with no link it is deleted.
    /// </summary>
    private async Task RetireUnshippedAsync(
        List<Visualizer> existing,
        HashSet<Ulid> shipped,
        CancellationToken ct
    )
    {
        List<Visualizer> stale = existing
            .Where(v => v.IsBuiltIn && !shipped.Contains(v.Id))
            .ToList();
        if (stale.Count == 0)
            return;

        List<Ulid> staleIds = stale.Select(v => v.Id).ToList();
        HashSet<Ulid> linked = (
            await context
                .VisualizerUsers.Where(link => staleIds.Contains(link.VisualizerId))
                .Select(link => link.VisualizerId)
                .Distinct()
                .ToListAsync(ct)
        ).ToHashSet();

        foreach (Visualizer visualizer in stale)
        {
            if (linked.Contains(visualizer.Id))
            {
                visualizer.IsBuiltIn = false;
                visualizer.Source = RetiredSource;
            }
            else
            {
                context.Visualizers.Remove(visualizer);
            }
        }

        await context.SaveChangesAsync(ct);
    }
}
