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

namespace NoMercy.Database.Models.Music;

/// <summary>
/// How a track's audio analysis ended.
/// <para>
/// A track that cannot be analyzed has to stop being picked up. Without a
/// terminal state, a corrupt or unreadable file is selected by every sweep
/// forever and the queue never drains.
/// </para>
/// </summary>
public enum AudioAnalysisState
{
    /// <summary>Queued, or claimed by a run that did not finish.</summary>
    Pending = 0,

    /// <summary>Analyzed. Individual measurements may still be null.</summary>
    Ok = 1,

    /// <summary>
    /// Analysis ran and failed. Retried only when the analyzer version changes,
    /// on the assumption that a new analyzer may succeed where the old one did not.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The file is not analyzable at all — no audio stream, or a codec the
    /// build cannot decode. Never retried by a sweep.
    /// </summary>
    Unsupported = 3,
}
