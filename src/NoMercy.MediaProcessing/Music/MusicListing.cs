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
/// One listing pass of a library root: the files of the last pass and of this one, how many
/// folders were listed, and the folders that could not be read and why.
/// </summary>
public sealed record MusicListing(
    IReadOnlyList<TrackFileEntry> Previous,
    IReadOnlyList<TrackFileEntry> Current,
    int ListedFolders,
    IReadOnlyList<(string Directory, Exception Error)> SkippedFolders
)
{
    public void Deconstruct(
        out IReadOnlyList<TrackFileEntry> previous,
        out IReadOnlyList<TrackFileEntry> current
    )
    {
        previous = Previous;
        current = Current;
    }
}
