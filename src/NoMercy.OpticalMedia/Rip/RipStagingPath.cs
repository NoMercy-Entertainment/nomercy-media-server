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

namespace NoMercy.OpticalMedia.Rip;

/// <summary>
/// Confines a client-supplied rip output path to the server's own rip staging
/// directory. ConfirmDisc opens, copies into a library, and then deletes the
/// supplied path; without this guard a Moderator-scoped caller could point it at
/// any file the service account can read (copying it into a servable library and
/// deleting the original).
/// </summary>
public static class RipStagingPath
{
    /// <summary>
    /// True only when <paramref name="ripOutputPath"/> canonicalizes to a location
    /// strictly inside <paramref name="stagingRoot"/>. Traversal (<c>..</c>),
    /// sibling-prefix (<c>ripper-evil</c> vs <c>ripper</c>), the root itself, and
    /// unparseable paths all return false.
    /// </summary>
    public static bool IsWithinStaging(string? ripOutputPath, string? stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(ripOutputPath) || string.IsNullOrWhiteSpace(stagingRoot))
            return false;

        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath(stagingRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            candidate = Path.GetFullPath(ripOutputPath);
        }
        catch
        {
            return false;
        }

        return candidate.Length > root.Length
            && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (
                candidate[root.Length] == Path.DirectorySeparatorChar
                || candidate[root.Length] == Path.AltDirectorySeparatorChar
            );
    }
}
