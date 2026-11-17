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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>What marks the real end of an episode's content.</summary>
public enum ContentEndEvidence
{
    /// <summary>Nothing runs to the end: content ends with the file.</summary>
    FileEnd,

    /// <summary>Plain black runs to the end.</summary>
    Black,

    /// <summary>Silence runs to the end.</summary>
    Silence,

    /// <summary>Black and silence both run to the end.</summary>
    BlackAndSilence,
}
