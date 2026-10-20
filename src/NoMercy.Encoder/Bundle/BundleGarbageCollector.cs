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

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Storage;

namespace NoMercy.Encoder.Bundle;

/// <summary>
/// Walks a library root for every per-media-item <c>.nomercy.json</c>
/// blueprint (see <see cref="MediaBlueprintWriter"/>) and flags any
/// <c>encodes[]</c> entry whose preset no longer exists in the database.
/// A leftover pre-blueprint <c>encodes/{slug}/manifest.json</c> file is
/// vestigial and simply does not match the blueprint filename — it is
/// never inspected and never crashes the sweep.
/// </summary>
public class BundleGarbageCollector(
    IStorage storage,
    IDbContextFactory<MediaContext> contextFactory,
    ILogger<BundleGarbageCollector> logger
) : IBundleGarbageCollector
{
    public async Task<IReadOnlyList<BundleOrphan>> SweepAsync(
        string libraryRoot,
        CancellationToken ct
    )
    {
        List<BundleOrphan> orphans = [];

        IReadOnlyList<StorageEntry> allFiles = storage.List(
            libraryRoot,
            pattern: null,
            recursive: true
        );

        if (allFiles.Count == 0)
            return orphans;

        List<StorageEntry> blueprintFiles = allFiles
            .Where(entry =>
                !entry.IsDirectory
                && string.Equals(
                    storage.GetName(entry.Path),
                    MediaBlueprintWriter.FileName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        if (blueprintFiles.Count == 0)
            return orphans;

        await using MediaContext db = await contextFactory.CreateDbContextAsync(ct);
        HashSet<string> knownPresetIds = db
            .EncodingPresets.AsNoTracking()
            .Select(p => p.Id.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (StorageEntry blueprintFile in blueprintFiles)
        {
            MediaBlueprint? blueprint = await TryReadBlueprintAsync(blueprintFile.Path, ct);
            if (blueprint is null)
                continue;

            // The positional record lets Newtonsoft hand over a null list when the
            // JSON carries no encodes (a hand-edited or partially written file).
            // Skip it like an unreadable one so every other blueprint still gets swept.
            if (blueprint.Encodes is null)
            {
                logger.LogWarning(
                    "Blueprint at {Path} has no encodes list — skipping",
                    blueprintFile.Path
                );
                continue;
            }

            string mediaFolder = storage.GetParent(blueprintFile.Path) ?? libraryRoot;

            foreach (BlueprintEncode encode in blueprint.Encodes)
            {
                if (knownPresetIds.Contains(encode.PresetId))
                    continue;

                orphans.Add(
                    new(
                        Path: string.IsNullOrEmpty(encode.OutputLocation)
                            ? mediaFolder
                            : encode.OutputLocation,
                        PresetSlug: encode.PresetSlug,
                        PresetId: encode.PresetId,
                        Reason: "preset deleted"
                    )
                );
            }
        }

        return orphans;
    }

    private async Task<MediaBlueprint?> TryReadBlueprintAsync(string path, CancellationToken ct)
    {
        try
        {
            byte[] bytes = await storage.ReadAsync(path, ct);
            return JsonConvert.DeserializeObject<MediaBlueprint>(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Could not read blueprint at {Path}: {Message} — skipping",
                path,
                ex.Message
            );
            return null;
        }
    }
}
