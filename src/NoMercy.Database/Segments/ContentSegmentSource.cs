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
/// The values <see cref="Models.Media.ContentSegment.Source"/> takes. Which of
/// them wins when two cover the same range is decided in
/// <see cref="ContentSegmentPrecedence"/>.
/// </summary>
public static class ContentSegmentSource
{
    /// <summary>Set or corrected by a person in the dashboard. Never overwritten.</summary>
    public const string Manual = "manual";

    /// <summary>Classified from the file's chapter list (title or length).</summary>
    public const string Chapters = "chapters";

    /// <summary>Community-provided data.</summary>
    public const string Import = "import";

    /// <summary>
    /// The retired chromaprint season detector. Nothing writes it any more; rows
    /// it stored stay readable and rank with the analysis.
    /// </summary>
    public const string Detector = "detector";

    /// <summary>
    /// The retired chromaprint detector of the encoder analysis endpoint. Nothing
    /// writes it any more; rows it stored stay readable and rank with the analysis.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>The audio and picture analysis (#598), the one automatic detector.</summary>
    public const string Analysis = "analysis";
}
