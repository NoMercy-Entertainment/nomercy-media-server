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

using System.Text.RegularExpressions;

namespace NoMercy.MediaProcessing.Reclaim;

public static partial class ReclaimClassifier
{
    private static readonly string[] OriginalExtensions =
    [
        ".mp4",
        ".mkv",
        ".m4v",
        ".avi",
        ".webm",
        ".mov",
    ];

    public static ReclaimClassification Classify(
        IReadOnlyList<FolderEntry> entries,
        bool isProtected,
        DateTimeOffset now,
        TimeSpan partialStaleAfter
    )
    {
        List<FolderEntry> ladders = entries
            .Where(entry => entry.IsDirectory && LadderDirectoryRegex().IsMatch(entry.Name))
            .ToList();

        List<FolderEntry> masters = entries
            .Where(entry =>
                !entry.IsDirectory
                && entry.Name.EndsWith(".NoMercy.m3u8", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (ladders.Count == 0 && masters.Count == 0)
            return new(ReclaimKind.None, [], 0);

        if (isProtected)
            return new(ReclaimKind.None, [], 0);

        bool hasOriginal = entries.Any(entry =>
            !entry.IsDirectory
            && OriginalExtensions.Contains(
                Path.GetExtension(entry.Name),
                StringComparer.OrdinalIgnoreCase
            )
            && !entry.Name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        );

        if (masters.Count > 0 && hasOriginal)
        {
            List<FolderEntry> targets = [.. ladders, .. masters];
            return new(
                ReclaimKind.ReclaimableHls,
                targets.Select(entry => entry.Name).ToList(),
                targets.Sum(entry => entry.Size)
            );
        }

        if (masters.Count == 0 && ladders.Count > 0)
        {
            DateTimeOffset staleThreshold = now - partialStaleAfter;
            bool allStale = ladders.All(entry => entry.LastModified < staleThreshold);

            if (!allStale)
                return new(ReclaimKind.None, [], 0);

            return new(
                ReclaimKind.OrphanPartial,
                ladders.Select(entry => entry.Name).ToList(),
                ladders.Sum(entry => entry.Size)
            );
        }

        return new(ReclaimKind.None, [], 0);
    }

    [GeneratedRegex(@"^(video_\d+x\d+(_.+)?|audio_[A-Za-z0-9_]+)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LadderDirectoryRegex();
}
