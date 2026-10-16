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

namespace NoMercy.Data.Plugins;

/// <summary>
/// How a plugin's library filter is read, the same way by every query.
/// <para>
/// No id asks for every library. An id asks for that library. An id that is
/// not a library id asks for nothing: reading it as no filter handed a plugin
/// the items of every library, which is the one answer a plugin working on
/// one library must never get.
/// </para>
/// </summary>
internal static class PluginLibraryFilter
{
    /// <summary>
    /// False when the id is not a library id, and the query answers an empty
    /// list. Otherwise <paramref name="library" /> is the library to filter
    /// on, or null for every library.
    /// </summary>
    public static bool TryRead(string? libraryId, out Ulid? library)
    {
        library = null;

        if (string.IsNullOrWhiteSpace(libraryId))
            return true;

        if (!Ulid.TryParse(libraryId, out Ulid parsed))
            return false;

        library = parsed;
        return true;
    }
}
