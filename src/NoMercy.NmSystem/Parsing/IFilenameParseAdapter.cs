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

namespace NoMercy.MediaProcessing.Files.Parsing;

/// <summary>
/// A single filename-parsing strategy. Adapters are ordered and tried in turn by
/// <see cref="IFilenameParserPipeline"/>; the first one that recognises the name
/// wins. Adapters are resolved from DI, so plugins can contribute their own and the
/// order/enabled set can be overridden at runtime via <see cref="FilenameParsingOptions"/>.
/// </summary>
public interface IFilenameParseAdapter
{
    /// <summary>Stable identifier used for ordering/enable overrides and diagnostics.</summary>
    string Name { get; }

    /// <summary>Default execution order (ascending). Lower runs first.</summary>
    int Order { get; }

    /// <summary>
    /// Attempts to parse <paramref name="context"/>. Returns a populated
    /// <see cref="MovieFile"/> on a match, or null to let the next adapter try.
    /// </summary>
    MovieFile? TryParse(ParseContext context);
}
