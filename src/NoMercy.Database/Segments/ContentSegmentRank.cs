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

namespace NoMercy.Database.Segments;

/// <summary>
/// The one precedence order for content segments: a higher value wins when two
/// segments cover the same kind of range (intro, recap, or outro/credits) of
/// the same episode or movie.
/// <para>
/// manual &gt; chapters by title &gt; import &gt; detector = analysis &gt;
/// chapters by length.
/// </para>
/// <para>
/// #503 sets manual &gt; chapters by title &gt; detector &gt; chapters by
/// length; #598 replaces the chromaprint "detector" with "analysis" and keeps
/// manual &gt; chapters &gt; analysis, so detector, auto and analysis share one
/// rank. Neither issue places "import"; it sits under the chapter titles and
/// above every automatic detection.
/// </para>
/// </summary>
public enum ContentSegmentRank
{
    /// <summary>A chapter chosen only by its length ("Chapter 02" of 90 s).</summary>
    ChapterLength = 1,

    /// <summary>Audio fingerprinting: "detector", "auto" and, later, "analysis".</summary>
    Detection = 2,

    /// <summary>Community-provided data, and any source this server does not know.</summary>
    Import = 3,

    /// <summary>A chapter whose title says what it is ("OP", "Ending").</summary>
    ChapterTitle = 4,

    /// <summary>Set by a person. Nothing automatic replaces it.</summary>
    Manual = 5,
}
