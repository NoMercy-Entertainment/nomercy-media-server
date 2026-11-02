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

namespace NoMercy.MediaProcessing.Files.Parsing;

/// <summary>
/// Runtime configuration for the parse pipeline. Lets operators reorder or disable
/// adapters without recompiling (bind from config / DB). Adapters named in
/// <see cref="Order"/> run first, in that order; the rest follow by their default
/// <see cref="IFilenameParseAdapter.Order"/>.
/// </summary>
public sealed class FilenameParsingOptions
{
    /// <summary>Adapter names, in the order they should run. Unlisted adapters keep their default order.</summary>
    public List<string> Order { get; set; } = new();

    /// <summary>Adapter names that should be skipped entirely.</summary>
    public HashSet<string> Disabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
