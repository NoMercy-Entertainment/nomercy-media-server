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

public class PlaylistPaletteSource : IPaletteSource
{
    public string EntityType => "playlist";

    public async Task<string?> CurrentPaletteAsync(
        MediaContext db,
        string entityId,
        CancellationToken ct
    )
    {
        Guid id = Guid.Parse(entityId);
        return await db
            .Playlists.Where(p => p.Id == id)
            .Select(p => p._colorPalette)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PaletteResult> GenerateAsync(
        MediaContext db,
        string entityId,
        CancellationToken ct
    )
    {
        Guid id = Guid.Parse(entityId);
        Playlist? playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist is null)
            return PaletteResult.NoImage();
        if (playlist.Cover is null)
            return PaletteResult.NoImage();

        string filePath = AppFiles.MusicImagesPath + playlist.Cover;
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
            .Playlists.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p._colorPalette, json), ct);
    }
}
