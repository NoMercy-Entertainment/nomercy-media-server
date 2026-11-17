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

namespace NoMercy.MediaProcessing.Segments;

/// <summary>How one analysis run ended.</summary>
public enum SegmentAnalysisOutcome
{
    /// <summary>
    /// Nothing could be read: no encoded file, the library folder unmounted, or
    /// a non-local driver. Not a failure; the next scan tries again.
    /// </summary>
    NoSources,

    /// <summary>
    /// Files were there but no episode's audio could be measured (FFmpeg missing,
    /// without chromaprint, or failing). Counts as a failure.
    /// </summary>
    NothingMeasured,

    /// <summary>
    /// Some episodes could not be measured (an unreadable file, a mount that
    /// dropped mid-run). The measured ones are stored, but the season is not
    /// done: this counts as a failure so a later run picks up the rest.
    /// </summary>
    PartlyMeasured,

    /// <summary>Every readable episode was analysed and the answers stored (possibly empty).</summary>
    Analysed,
}
