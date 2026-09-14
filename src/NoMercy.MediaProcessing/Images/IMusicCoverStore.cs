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

using NoMercy.Database.Models.Libraries;

namespace NoMercy.MediaProcessing.Images;

public interface IMusicCoverStore
{
    /// <summary>
    /// Writes the served cover for <paramref name="slug"/> and returns its stored
    /// path and color palette.
    /// </summary>
    Task<SavedMusicCover> SaveAsync(string slug, Stream image, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="fileName"/> into the media's folder inside its library.
    /// Returns false when the library folder cannot be resolved.
    /// </summary>
    Task<bool> SaveToLibraryAsync(
        Folder libraryFolder,
        string hostFolder,
        string fileName,
        Stream image,
        CancellationToken ct = default
    );
}
