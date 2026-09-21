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

namespace NoMercy.Plugins.Abstractions;

/// <summary>One thing the caller watched, and how far they got.</summary>
public sealed record PluginWatchEntry
{
    public required MediaId Media { get; init; }
    public required DateTimeOffset WatchedAt { get; init; }
    public TimeSpan Position { get; init; }
    public bool Finished { get; init; }
}
