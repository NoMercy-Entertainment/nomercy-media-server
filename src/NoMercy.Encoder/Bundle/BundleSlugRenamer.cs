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
using Newtonsoft.Json.Linq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Storage;

namespace NoMercy.Encoder.Bundle;

/// <summary>
/// Startup-time pass that relabels the <c>preset_slug</c> recorded inside a
/// media item's <c>.nomercy.json</c> blueprint when a built-in preset's
/// display name (and therefore its computed slug) changes between releases.
///
/// The slug lives purely as a data field on each <c>encodes[]</c> entry —
/// unlike the retired per-preset <c>encodes/{slug}/</c> layout, there is no
/// directory to rename and no artifact to move.
///
/// For each <c>oldSlug → newSlug</c> pair supplied, the renamer:
/// <list type="bullet">
///   <item>Walks every library folder for every <c>.nomercy.json</c> file.</item>
///   <item>Rewrites any <c>encodes[].preset_slug</c> matching <c>oldSlug</c>
///   to <c>newSlug</c> and saves the file.</item>
/// </list>
///
/// The pass is idempotent: once an entry's slug has been rewritten it no
/// longer matches <c>oldSlug</c>, so a second run is a no-op.
/// </summary>
public class BundleSlugRenamer(
    IReadOnlyDictionary<string, string> slugMap,
    IStorageFactory storageFactory,
    MediaContext context,
    ILogger<BundleSlugRenamer> logger
)
{
    /// <summary>
    /// Runs the rewrite pass. Reads all library folders from the database,
    /// then for each folder rewrites every blueprint that references an old
    /// slug.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (slugMap.Count == 0)
            return;

        // Guard: empty/whitespace slugs would match every entry whose
        // preset_slug is itself empty and would rewrite it to an equally
        // meaningless value. Slugs come from BuiltinPresets which never
        // emits empty strings, but a faulty override map must NOT corrupt
        // a library's blueprints.
        Dictionary<string, string> validPairs = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in slugMap)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                logger.LogWarning(
                    "BundleSlugRenamer: skipping empty slug pair '{Key}' → '{Value}'",
                    pair.Key,
                    pair.Value
                );
                continue;
            }

            validPairs[pair.Key] = pair.Value;
        }

        if (validPairs.Count == 0)
            return;

        List<Folder> folders = await context
            .Folders.Include(f => f.Driver)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (Folder folder in folders)
        {
            IStorage storage = storageFactory.For(folder.Id, folder.DriverId, folder.Path);
            await RewriteFolderAsync(storage, folder.Path, validPairs, ct);
        }
    }

    private async Task RewriteFolderAsync(
        IStorage storage,
        string folderPath,
        IReadOnlyDictionary<string, string> validPairs,
        CancellationToken ct
    )
    {
        IReadOnlyList<StorageEntry> allFiles;
        try
        {
            allFiles = storage.List(string.Empty, pattern: null, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "BundleSlugRenamer: failed to list '{FolderPath}': {Message}",
                folderPath,
                ex.Message
            );
            return;
        }

        foreach (StorageEntry entry in allFiles)
        {
            if (entry.IsDirectory)
                continue;
            if (
                !string.Equals(
                    storage.GetName(entry.Path),
                    MediaBlueprintWriter.FileName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;

            await RewriteBlueprintAsync(storage, entry.Path, folderPath, validPairs, ct);
        }
    }

    private async Task RewriteBlueprintAsync(
        IStorage storage,
        string blueprintPath,
        string folderPath,
        IReadOnlyDictionary<string, string> validPairs,
        CancellationToken ct
    )
    {
        try
        {
            string json = await storage.ReadAllTextAsync(blueprintPath, ct);
            using StringReader stringReader = new(json);
            using JsonTextReader jsonReader = new(stringReader)
            {
                DateParseHandling = DateParseHandling.None,
            };
            JObject blueprint = JObject.Load(jsonReader);
            if (blueprint?["encodes"] is not JArray encodes)
                return;

            bool changed = false;
            foreach (JToken encode in encodes)
            {
                string? currentSlug = encode["preset_slug"]?.Value<string>();
                if (
                    string.IsNullOrWhiteSpace(currentSlug)
                    || !validPairs.TryGetValue(currentSlug, out string? newSlug)
                )
                    continue;

                encode["preset_slug"] = newSlug;
                changed = true;
            }

            if (!changed)
                return;

            string updated = JsonConvert.SerializeObject(blueprint, Formatting.Indented);
            await storage.WriteAsync(blueprintPath, Encoding.UTF8.GetBytes(updated), ct);

            logger.LogInformation(
                "BundleSlugRenamer: rewrote preset_slug in '{Path}' ('{FolderPath}')",
                blueprintPath,
                folderPath
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "BundleSlugRenamer: failed to rewrite blueprint '{Path}' in '{FolderPath}': {Message}",
                blueprintPath,
                folderPath,
                ex.Message
            );
        }
    }
}
