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
using NoMercy.Database;
using NoMercy.Database.Models.Encoder;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

public sealed class IncompleteEncodeRecorder
{
    /// <summary>
    /// Records a failed finish and counts it: stores the existing row's AttemptsMade + 1,
    /// or 1 for a new row. The queue row cannot give this total because the encode job
    /// resets its attempts on every phase advance. ClearAsync resets the count on success.
    /// </summary>
    public async Task RecordFailureAsync(
        MediaContext context,
        long mediaId,
        string folderId,
        string title,
        IReadOnlyList<string> missingKeys,
        string? lastError,
        CancellationToken ct
    )
    {
        int previousAttempts = await context
            .IncompleteEncodes.AsNoTracking()
            .Where(r => r.MediaId == mediaId && r.FolderId == folderId)
            .Select(r => r.AttemptsMade)
            .FirstOrDefaultAsync(ct);

        await RecordAsync(
            context,
            mediaId,
            folderId,
            title,
            missingKeys,
            lastError,
            previousAttempts + 1,
            ct
        );
    }

    public async Task RecordAsync(
        MediaContext context,
        long mediaId,
        string folderId,
        string title,
        IReadOnlyList<string> missingKeys,
        string? lastError,
        int attemptsMade,
        CancellationToken ct
    )
    {
        string renditions = string.Join('\n', missingKeys);

        IncompleteEncode? existing = await context.IncompleteEncodes.FirstOrDefaultAsync(
            r => r.MediaId == mediaId && r.FolderId == folderId,
            ct
        );

        if (existing is null)
        {
            DateTime now = DateTime.UtcNow;
            context.IncompleteEncodes.Add(
                new()
                {
                    MediaId = mediaId,
                    FolderId = folderId,
                    Title = title,
                    MissingRenditions = renditions,
                    LastError = lastError,
                    AttemptsMade = attemptsMade,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                }
            );
        }
        else
        {
            existing.MissingRenditions = renditions;
            existing.LastError = lastError;
            existing.AttemptsMade = attemptsMade;
            existing.LastSeenAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(
        MediaContext context,
        long mediaId,
        string folderId,
        CancellationToken ct
    )
    {
        await context
            .IncompleteEncodes.Where(r => r.MediaId == mediaId && r.FolderId == folderId)
            .ExecuteDeleteAsync(ct);
    }
}
