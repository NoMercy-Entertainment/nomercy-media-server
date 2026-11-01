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

using NoMercy.MediaProcessing.Files.Parsing.Adapters;

namespace NoMercy.MediaProcessing.Files.Parsing;

/// <summary>
/// The adapters the server ships, in the order they run. The container registers
/// the same list; code that is built without the container (the library scan)
/// reads names through <see cref="Resolver"/> so it cannot drift from it.
/// </summary>
public static class BuiltInFilenameParser
{
    public static FilenameResolver Resolver { get; } = new(new FilenameParserPipeline(Adapters()));

    public static IFilenameParseAdapter[] Adapters() =>
        [
            new EpisodePrefixAdapter(),
            new EpisodeWordAdapter(),
            new SeasonEpisodeAdapter(),
            new SeasonSpecialAdapter(),
            new CrossFormatAdapter(),
            new AnimeAbsoluteAdapter(),
            new EpisodeShortFormAdapter(),
            new SpecialsAdapter(),
            new SeasonPackAdapter(),
            new PartAdapter(),
            new MovieDetectorAdapter(),
        ];
}
