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

using MovieFileLibrary;

namespace NoMercy.MediaProcessing.Files.Parsing.Adapters;

/// <summary>Terminal fallback: delegates to the MovieFileLibrary detector, which
/// always returns a best-effort result. Ordered last so the targeted series
/// matchers get first refusal.</summary>
public sealed class MovieDetectorAdapter : IFilenameParseAdapter
{
    public string Name => "movie-detector";
    public int Order => int.MaxValue;

    public MovieFile? TryParse(ParseContext context)
    {
        MovieDetector detector = new();
        return detector.GetInfo(context.Title);
    }
}
