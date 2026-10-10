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

using ImageMagick;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Information;

namespace NoMercy.MediaProcessing.Images.Palettes.Sources;

public class ReleaseGroupPaletteSource : IPaletteSource
{
    public string EntityType => "releasegroup";

    public async Task<string?> CurrentPaletteAsync(
        MediaContext db,
        string entityId,
        CancellationToken ct
    )
    {
        Guid id = Guid.Parse(entityId);
        return await db
            .ReleaseGroups.Where(r => r.Id == id)
            .Select(r => r._colorPalette)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PaletteResult> GenerateAsync(
        MediaContext db,
        string entityId,
        CancellationToken ct
    )
    {
        Guid id = Guid.Parse(entityId);
        ReleaseGroup? releaseGroup = await db.ReleaseGroups.FirstOrDefaultAsync(
            r => r.Id == id,
            ct
        );
        if (releaseGroup is null)
            return PaletteResult.NoImage();
        if (releaseGroup.Cover is null)
            return PaletteResult.NoImage();

        string filePath = AppFiles.MusicImagesPath + releaseGroup.Cover;
        if (!File.Exists(filePath))
            return PaletteResult.NoImage();

        using MagickImage image = new();
        await image.ReadAsync(filePath, ct);
        string json = BaseImageManager.GenerateColorPalette([
            new() { Key = "cover", ImageData = image },
        ]);
        return PaletteResult.Success(json);
    }

    public async Task PersistAsync(
        MediaContext db,
        string entityId,
        string json,
        CancellationToken ct
    )
    {
        Guid id = Guid.Parse(entityId);
        await db
            .ReleaseGroups.Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r._colorPalette, json), ct);
    }
}
