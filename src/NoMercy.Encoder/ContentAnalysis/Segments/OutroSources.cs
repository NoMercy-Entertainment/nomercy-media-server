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

/// <summary>What an outro block was built from.</summary>
[Flags]
public enum OutroSources
{
    None = 0,

    /// <summary>Credits seen in the picture (dark frames and/or text).</summary>
    Credits = 1,

    /// <summary>An ending theme shared with other episodes.</summary>
    Audio = 2,

    /// <summary>Under 8 s of logos and end cards after it, folded in.</summary>
    Logos = 4,

    /// <summary>Its start moved later to the season's median credits length.</summary>
    SeasonLength = 8,
}
