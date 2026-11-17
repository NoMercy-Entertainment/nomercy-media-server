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

/// <summary>How a chapter was recognised. Title outranks Length (see ContentSegmentRank).</summary>
public enum ChapterSegmentMethod
{
    /// <summary>The chapter's title says what it is ("OP", "Ending").</summary>
    Title,

    /// <summary>The titles are generic ("Chapter 02") and the length decided.</summary>
    Length,
}
