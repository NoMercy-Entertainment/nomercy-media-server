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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Storage;

namespace NoMercy.Data.Services;

/// <summary>
/// Repairs the tracks an older importer run stored with a <c>HostFolder</c>
/// that holds the album folder twice — once with forward slashes, once with
/// backslashes. <c>Filename</c> is the bare file name, so every consumer that
/// combines the two (playback, subtitles, transcodes, the audio analysis job,
/// the plugin audio tools) builds a path that exists nowhere, and the analysis
/// of such a track fails on every sweep, for good.
/// <para>
/// A row is only rewritten when the two halves are the same folder <i>and</i>
/// the file is where the repaired folder says it is: a guess that moved a row
/// onto a path nobody checked would be worse than the broken value it replaced.
/// Anything else is counted and left exactly as it was.
/// </para>
/// <para>
/// Idempotent: a repaired row no longer carries a second root, so a second run
/// does not select it, and a healthy database costs one indexed query.
/// </para>
/// </summary>
public class DoubledHostFolderRepair(
    IDbContextFactory<MediaContext> mediaContextFactory,
    IStorageDriver storageDriver,
    ILogger<DoubledHostFolderRepair> logger
) : IDoubledHostFolderRepair
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await using MediaContext mediaContext = await mediaContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        // Coarse, translatable pre-filter: a second root always shows up as a
        // colon or a doubled separator past the folder's own leading root,
        // which is at most the first two characters ("Q:", "//"). The exact
        // decision is HostFolderPath's, on the handful of rows this returns.
        List<Track> candidates = await mediaContext
            .Tracks.Where(track =>
                track.HostFolder != null
                && track.HostFolder.Length > 2
                && (
                    track.HostFolder.Substring(2).Contains(":")
                    || track.HostFolder.Substring(2).Contains("//")
                    || track.HostFolder.Substring(2).Contains(@"\\")
                )
            )
            .ToListAsync(cancellationToken);

        int repaired = 0;
        int leftAloneFileMissing = 0;
        int leftAloneHalvesDiffer = 0;

        foreach (Track track in candidates)
        {
            string hostFolder = track.HostFolder!;

            if (!HostFolderPath.ContainsSecondRoot(hostFolder))
                continue;

            string? singleFolder = HostFolderPath.RepairDoubled(hostFolder);
            if (singleFolder is null)
            {
                leftAloneHalvesDiffer++;
                logger.LogWarning(
                    "Track {TrackId} keeps a host folder holding two different rooted paths: '{HostFolder}'",
                    [track.Id, hostFolder]
                );
                continue;
            }

            string path = storageDriver.CombinePath(singleFolder, track.Filename ?? string.Empty);
            if (!storageDriver.FileExists(path))
            {
                leftAloneFileMissing++;
                logger.LogWarning(
                    "Track {TrackId} keeps its doubled host folder: nothing is at '{Path}'",
                    [track.Id, path]
                );
                continue;
            }

            track.HostFolder = singleFolder;
            repaired++;
        }

        if (repaired > 0)
            await mediaContext.SaveChangesAsync(cancellationToken);

        if (repaired > 0 || leftAloneFileMissing > 0 || leftAloneHalvesDiffer > 0)
            logger.LogInformation(
                "Doubled host folder sweep: {Repaired} repaired, {FileMissing} left alone because the file is not there, {HalvesDiffer} left alone because the halves differ",
                [repaired, leftAloneFileMissing, leftAloneHalvesDiffer]
            );

        return repaired;
    }
}
