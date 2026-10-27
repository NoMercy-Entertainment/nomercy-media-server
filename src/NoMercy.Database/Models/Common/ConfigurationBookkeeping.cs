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

namespace NoMercy.Database.Models.Common;

/// <summary>
/// Configuration rows a startup seed writes to remember its own progress. They are
/// not user settings, so anything that lists settings leaves them out.
/// </summary>
public static class ConfigurationBookkeeping
{
    public const string GenreTranslationMissPrefix = "genre_translation_miss:";
    public const string SeedStatePrefix = "seed_state:";

    public static bool IsBookkeepingKey(string key) =>
        key.StartsWith(GenreTranslationMissPrefix, StringComparison.Ordinal)
        || key.StartsWith(SeedStatePrefix, StringComparison.Ordinal);
}
