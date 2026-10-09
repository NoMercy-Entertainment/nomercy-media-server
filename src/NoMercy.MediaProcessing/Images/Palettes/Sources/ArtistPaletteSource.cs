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

public class ArtistPaletteSource : IPaletteSource
{
    public string EntityType => "artist";

    public async Task<string?> CurrentPaletteAsync(
        MediaContext db,
        string entityId,
        CancellationToken ct
    )
    {
        Guid id = Guid.Parse(entityId);
        return await db
            .Artists.Where(a => a.Id == id)
            .Select(a => a._colorPalette)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PaletteResult> GenerateAsync(
        MediaContext db,
        string entityId,
        CancellationToken ct
    )
    {
        Guid id = Guid.Parse(entityId);
        Artist? artist = await db.Artists.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (artist is null)
            return PaletteResult.NoImage();
        if (artist.Cover is null)
            return PaletteResult.NoImage();

        string filePath = AppFiles.MusicImagesPath + artist.Cover;
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
            .Artists.Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a._colorPalette, json), ct);
    }
}
