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

namespace NoMercy.Plugin.Cli;

/// <param name="SourceFolder">Plugin source. Never written to.</param>
/// <param name="OutputFolder">Receives exactly one zip and plugin.json.</param>
/// <param name="FeedFolder">The only NuGet source the restore may use.</param>
/// <param name="PackagesFolder">Where restored packages are extracted; the NuGet default when null.</param>
public sealed record PackOptions(
    string SourceFolder,
    string OutputFolder,
    string? FeedFolder,
    string? PackagesFolder = null
);
