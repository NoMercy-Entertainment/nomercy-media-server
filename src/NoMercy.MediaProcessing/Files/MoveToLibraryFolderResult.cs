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

namespace NoMercy.MediaProcessing.Files;

/// <summary>
/// How a move to another library ended. Only <see cref="Moved"/> leaves the
/// files, the title's rows and the rescan in agreement; every other value names
/// where the move stopped so the caller can report that instead of success.
/// </summary>
public enum MoveToLibraryFolderResult
{
    Moved,

    /// <summary>The title's folder was not found under any folder of its library.</summary>
    SourceFolderNotFound,

    /// <summary>The target folder is linked to no library, so there is nothing to repoint the title at.</summary>
    TargetFolderHasNoLibrary,

    /// <summary>The files reached the target folder but the rows still name the old library; the log carries both paths.</summary>
    DatabaseUpdateFailed,
}
