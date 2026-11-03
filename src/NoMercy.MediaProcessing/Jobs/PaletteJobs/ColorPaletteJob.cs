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

using NoMercy.Database;
using NoMercy.MediaProcessing.Images.Palettes;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Jobs.PaletteJobs;

[Serializable]
public class ColorPaletteJob : IShouldQueue
{
    public string QueueName => "palette";
    public int Priority => PalettePriority.ForImport(EntityType);

    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;

    public ColorPaletteJob() { }

    public ColorPaletteJob(string entityType, string entityId)
    {
        EntityType = entityType;
        EntityId = entityId;
    }

    public Task Handle() => HandleCore(null, null);

    /// <summary>
    /// Executes palette generation using the provided context and registry.
    /// Exposed for unit testing; production path passes null to use defaults.
    /// </summary>
    internal async Task HandleCore(
        MediaContext? dbOverride,
        PaletteSourceRegistry? registryOverride
    )
    {
        MediaContext db = dbOverride ?? new MediaContext();
        bool ownsDb = dbOverride is null;
        try
        {
            PaletteSourceRegistry registry =
                registryOverride ?? DefaultPaletteSourceRegistry.Instance;

            IPaletteSource? source = registry.Resolve(EntityType);
            if (source is null)
                return;

            // "{}" is not "unpainted" — it is the marker PaletteResult.NoImage
            // persists for an entity that has no image to sample, and it says so
            // with Permanent: true. Treating it as pending re-ran generation on
            // every pass, wrote "{}" again, and left the row looking pending
            // forever: 50,622 images and 494 people on the dev library churned
            // like this indefinitely. A palette with colours is terminal too.
            // What is not terminal is a colourless entry such as {"poster":{}}:
            // older builds stored that for a failed image download, so it is a
            // retry, not an answer. Transient failures now throw before they can
            // persist (see below).
            string? current = await source.CurrentPaletteAsync(
                db,
                EntityId,
                CancellationToken.None
            );
            if (ColorPalette.IsFinished(current))
                return;

            // Transient failures (network, IO, timeout) propagate as exceptions so the
            // queue worker retries with backoff. Only a returned PaletteResult triggers a
            // persist — a thrown exception never writes "{}".
            PaletteResult result = await source.GenerateAsync(db, EntityId, CancellationToken.None);
            await source.PersistAsync(db, EntityId, result.Json, CancellationToken.None);
        }
        finally
        {
            if (ownsDb)
                await db.DisposeAsync();
        }
    }
}
