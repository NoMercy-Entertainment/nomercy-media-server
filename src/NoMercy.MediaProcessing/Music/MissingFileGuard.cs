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
/// Decides when a pass sees so many files missing at once that the listing, not the
/// library, is probably wrong (an unmounted share, a half-listed folder).
/// </summary>
public static class MissingFileGuard
{
    public const int MaxMissingFiles = 500;
    public const double MaxMissingFraction = 0.10;

    // A library of a handful of files would trip the fraction rule on one deleted file.
    public const int MinMissingForFractionRule = 5;

    public static bool ShouldSkipDeletes(int knownFiles, int missingFiles)
    {
        if (missingFiles > MaxMissingFiles)
            return true;

        return missingFiles >= MinMissingForFractionRule
            && missingFiles > knownFiles * MaxMissingFraction;
    }
}
