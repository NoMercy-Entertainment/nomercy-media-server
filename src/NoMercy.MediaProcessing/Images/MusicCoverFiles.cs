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
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.CoverArt.Client;
using NoMercy.Providers.FanArt.Client;

namespace NoMercy.MediaProcessing.Images;

/// <summary>
/// The only way a music cover name reaches a <c>Cover</c> column: the file is
/// downloaded into the music images folder first, and the name is handed back
/// only for a file that is really there. A name written without the file
/// answers 404 at <c>/images/music/{file}</c> for good.
/// </summary>
public class MusicCoverFiles
{
    public virtual Task<bool> IsStoredAsync(string cover) =>
        FanArtImageClient.IsStored(Path.GetFileName(cover));

    /// <returns>The stored file name of the first candidate that downloaded, or <c>null</c>.</returns>
    public Task<string?> StoreFirstFanArtAsync(IEnumerable<Uri> candidates) =>
        StoreFirstAsync(candidates, DownloadFanArtAsync);

    /// <returns>The stored file name of the first candidate that downloaded, or <c>null</c>.</returns>
    public Task<string?> StoreFirstCoverArtAsync(IEnumerable<Uri> candidates) =>
        StoreFirstAsync(candidates, DownloadCoverArtAsync);

    protected virtual async Task<bool> DownloadFanArtAsync(Uri url)
    {
        using MagickImage? image = await FanArtImageClient.Download(url);
        return image is not null;
    }

    protected virtual async Task<bool> DownloadCoverArtAsync(Uri url)
    {
        using MagickImage? image = await CoverArtCoverArtClient.Download(url);
        return image is not null;
    }

    private static async Task<string?> StoreFirstAsync(
        IEnumerable<Uri> candidates,
        Func<Uri, Task<bool>> download
    )
    {
        foreach (Uri url in candidates)
        {
            try
            {
                if (await download(url))
                    return "/" + url.FileName();
            }
            catch (Exception)
            {
                // A dead or non-image candidate must not stop the next one from being tried.
            }
        }

        return null;
    }
}
