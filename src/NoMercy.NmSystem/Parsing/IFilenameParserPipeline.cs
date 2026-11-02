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

/// <summary>Runs the ordered, enabled filename parse adapters.</summary>
public interface IFilenameParserPipeline
{
    /// <summary>Runs the adapters in effective order and returns the first match.</summary>
    MovieFile Parse(ParseContext context);
}
