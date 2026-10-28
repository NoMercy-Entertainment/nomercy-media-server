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

using NoMercy.Database.Models.Music;

namespace NoMercy.MediaProcessing.Recordings;

public interface IRecordingRepository
{
    Task Store(Track recording, bool update = false);

    /// <summary>
    /// Points every Track row addressing the given file at <paramref name="to"/>; returns the row count.
    /// </summary>
    Task<int> RepointTrackFile(string fromHostFolder, string fromFilename, TrackFilePlacement to);

    Task LinkToRelease(AlbumTrack trackRelease);
    Task LinkToArtist(ArtistTrack insert);
    Task LinkToLibrary(LibraryTrack libraryTrack);
    Task LinkToLibrary(ArtistLibrary artistLibrary);
}
