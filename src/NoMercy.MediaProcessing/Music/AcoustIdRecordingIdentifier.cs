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

using Microsoft.Extensions.Logging;
using NoMercy.Providers.AcoustId;
using NoMercy.Providers.AcoustId.Client;
using NoMercy.Providers.AcoustId.Models;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Music;

public sealed class AcoustIdRecordingIdentifier(
    IAudioFingerprinter audioFingerprinter,
    ILogger<AcoustIdRecordingIdentifier> logger
) : IRecordingIdentifier
{
    public async Task<IReadOnlyCollection<Guid>> IdentifyAsync(
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
            using AcoustIdFingerprintClient client = new(audioFingerprinter);
            AcoustIdFingerprint? result = await client.Lookup(lease.Path);

            return
            [
                .. (result?.Results ?? [])
                    .SelectMany(item => item.Recordings ?? [])
                    .OfType<AcoustIdFingerprintRecording>()
                    .Select(recording => recording.Id)
                    .Where(id => id != Guid.Empty)
                    .Distinct(),
            ];
        }
        catch (Exception ex)
        {
            logger.LogInformation("Fingerprint lookup failed for {Path}: {Message}", path, ex.Message);
            return [];
        }
    }
}
