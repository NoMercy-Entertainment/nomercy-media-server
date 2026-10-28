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

namespace NoMercy.MediaProcessing.Music;

/// <summary>
/// Turns the previous and the current listing of a library into file actions: a path
/// that vanished next to a new path with the same size and modified time is a move,
/// the rest of the vanished paths are gone, the rest of the new paths are added.
/// </summary>
public static class MusicListingDiff
{
    public sealed record Result(
        IReadOnlyList<(TrackFileEntry From, TrackFileEntry To)> Moves,
        IReadOnlyList<TrackFileEntry> Gone,
        IReadOnlyList<TrackFileEntry> Added
    );

    public static Result Diff(
        IReadOnlyCollection<TrackFileEntry> previous,
        IReadOnlyCollection<TrackFileEntry> current
    )
    {
        HashSet<string> currentPaths = [.. current.Select(entry => entry.Path)];
        HashSet<string> previousPaths = [.. previous.Select(entry => entry.Path)];

        List<TrackFileEntry> gone = [.. previous.Where(entry => !currentPaths.Contains(entry.Path))];
        List<TrackFileEntry> added = [.. current.Where(entry => !previousPaths.Contains(entry.Path))];

        // An identity shared by two vanished or two new files proves nothing; those
        // are left to the caller's content check.
        ILookup<(long, long), TrackFileEntry> goneByIdentity = gone.Where(e => e.Identity is not null)
            .ToLookup(e => e.Identity!.Value);
        ILookup<(long, long), TrackFileEntry> addedByIdentity = added.Where(e => e.Identity is not null)
            .ToLookup(e => e.Identity!.Value);

        List<(TrackFileEntry From, TrackFileEntry To)> moves = [];
        foreach (IGrouping<(long, long), TrackFileEntry> group in goneByIdentity)
        {
            TrackFileEntry[] from = [.. group];
            TrackFileEntry[] to = [.. addedByIdentity[group.Key]];
            if (from.Length == 1 && to.Length == 1)
                moves.Add((from[0], to[0]));
        }

        HashSet<string> movedFrom = [.. moves.Select(move => move.From.Path)];
        HashSet<string> movedTo = [.. moves.Select(move => move.To.Path)];

        return new(
            moves,
            [.. gone.Where(entry => !movedFrom.Contains(entry.Path))],
            [.. added.Where(entry => !movedTo.Contains(entry.Path))]
        );
    }
}
