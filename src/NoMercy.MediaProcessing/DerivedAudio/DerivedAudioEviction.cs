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
        List<DerivedAudioRow> chosen = [];
        foreach (DerivedAudioRow row in Candidates(rows, capBytes, grace, now))
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

    /// <summary>
    /// Every row eviction is allowed to take, least recently used first.
    /// <see cref="Choose" /> is this list cut off at the cap; the store walks
    /// the whole list, because a row can be in use again by the time its turn
    /// comes and the cap still has to be met.
    /// </summary>
    public static IReadOnlyList<DerivedAudioRow> Candidates(
        IReadOnlyCollection<DerivedAudioRow> rows,
        long capBytes,
        TimeSpan grace,
        DateTime now
    )
    {
        if (rows.Sum(row => row.Bytes) <= capBytes)
        {
            return [];
        }

        DateTime cutoff = now - grace;
        return rows.Where(row => row.LastUsedAt <= cutoff).OrderBy(row => row.LastUsedAt).ToList();
    }
}
