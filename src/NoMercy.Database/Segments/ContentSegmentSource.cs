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

    /// <summary>The chromaprint season detector that runs after an encode.</summary>
    public const string Detector = "detector";

    /// <summary>The chromaprint detector started from the encoder analysis endpoint.</summary>
    public const string Auto = "auto";

    /// <summary>The audio and picture analysis that replaces the chromaprint detector (#598).</summary>
    public const string Analysis = "analysis";
}
