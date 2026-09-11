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

using DerivedAudioRow = NoMercy.Database.Models.Music.DerivedAudio;

namespace NoMercy.MediaProcessing.DerivedAudio;

/// <summary>
/// Which register rows to evict: the least recently used first, until the
/// total is under the cap, never a row used inside the grace window. A pure
/// function over rows so the policy is tested without a disk.
/// </summary>
/// <remarks>
/// Rows are aliased to <c>DerivedAudioRow</c> because this namespace shares
/// its last segment with <see cref="NoMercy.Database.Models.Music.DerivedAudio"/> —
/// the bare name would resolve to the namespace, not the type (CS0118).
/// </remarks>
public static class DerivedAudioEviction
{
    public static IReadOnlyList<DerivedAudioRow> Choose(
        IReadOnlyCollection<DerivedAudioRow> rows,
        long capBytes,
        TimeSpan grace,
        DateTime now
    )
    {
        long total = rows.Sum(row => row.Bytes);
        if (total <= capBytes)
        {
            return [];
        }

        DateTime cutoff = now - grace;
        List<DerivedAudioRow> chosen = [];
        foreach (
            DerivedAudioRow row in rows.Where(row => row.LastUsedAt <= cutoff)
                .OrderBy(row => row.LastUsedAt)
        )
        {
            if (total <= capBytes)
            {
                break;
            }
            chosen.Add(row);
            total -= row.Bytes;
        }
        return chosen;
    }
}
