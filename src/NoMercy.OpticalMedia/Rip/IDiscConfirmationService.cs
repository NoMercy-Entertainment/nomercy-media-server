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

using NoMercy.OpticalMedia.Sources;

namespace NoMercy.OpticalMedia.Rip;

/// <summary>
/// Applies a user-chosen TMDB/MusicBrainz match to an already-ripped file: renames/moves it
/// into the target library folder and triggers a library refresh so the import pipeline picks
/// it up. Pure move-and-notify — the caller has already validated the rip output exists.
/// </summary>
public interface IDiscConfirmationService
{
    Task<DiscConfirmationResult> ConfirmAsync(
        Ulid folderId,
        Ulid libraryId,
        string ripOutputPath,
        CustomMetadata metadata,
        CancellationToken ct
    );
}
