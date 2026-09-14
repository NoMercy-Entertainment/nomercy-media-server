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

using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Images;

/// <summary>
/// Stores music cover art: the served copy under the app images folder, and
/// optionally a copy next to the media in its library folder.
/// </summary>
public class MusicCoverStore(IStorageDriver storageDriver, IStorageFactory storageFactory)
    : IMusicCoverStore
{
    public async Task<SavedMusicCover> SaveAsync(
        string slug,
        Stream image,
        CancellationToken ct = default
    )
    {
        string filePath = Path.Combine(AppFiles.ImagesPath, "music", slug + ".jpg");
        await using (Stream target = storageDriver.OpenWrite(filePath, overwrite: true))
            await image.CopyToAsync(target, ct);

        string colorPalette = await CoverArtImageManagerManager.ColorPalette(
            "cover",
            new(filePath)
        );
        return new($"/{slug}.jpg", colorPalette);
    }

    public async Task<bool> SaveToLibraryAsync(
        Folder libraryFolder,
        string hostFolder,
        string fileName,
        Stream image,
        CancellationToken ct = default
    )
    {
        IStorage folderStorage = storageFactory.For(
            libraryFolder.Id,
            libraryFolder.DriverId,
            string.Empty
        );
        // The driver, not the facade: the facade's GetFullPath only works for local
        // storage and throws on every remote backend.
        string libraryRoot = folderStorage.Driver.GetFullPath(libraryFolder.Path);
        if (string.IsNullOrEmpty(libraryRoot))
            return false;

        string filePath = Path.Combine(libraryRoot, hostFolder.TrimStart('\\'), fileName);
        await using Stream target = folderStorage.Driver.OpenWrite(filePath, overwrite: true);
        await image.CopyToAsync(target, ct);
        return true;
    }
}
