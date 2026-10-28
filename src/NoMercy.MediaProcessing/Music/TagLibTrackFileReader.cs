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

using NoMercy.NmSystem.Dto;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Music;

public sealed class TagLibTrackFileReader : ITrackFileReader
{
    public async Task<TrackFileFacts?> ReadAsync(
        IStorageDriver driver,
        string path,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using LocalPathLease lease = await driver.AcquireLocalPathAsync(
                path,
                cancellationToken
            );
            TagFile tagFile = TagFile.Create(lease.Path);

            return new(
                (int)(tagFile.Tag?.Track ?? 0),
                tagFile.Properties?.Duration ?? TimeSpan.Zero
            );
        }
        catch (Exception)
        {
            return null;
        }
    }
}
