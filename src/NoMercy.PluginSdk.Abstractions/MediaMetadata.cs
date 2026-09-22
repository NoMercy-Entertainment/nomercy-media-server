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

namespace NoMercy.PluginSdk.Abstractions;

public class MediaMetadata
{
    public required string Title { get; init; }
    public string? Overview { get; init; }
    public int? Year { get; init; }
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
    public List<string> Genres { get; init; } = [];
    public double? Rating { get; init; }
    public string? ExternalId { get; init; }
    public string? ExternalSource { get; init; }
}
