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

/// <summary>
/// What to proxy, in the order to try it.
/// </summary>
public sealed record PluginProxyRequest
{
    public required IReadOnlyList<PluginProxyLink> Links { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public Uri? Cover { get; init; }
    public bool PassRange { get; init; } = true;
    public bool RewriteHlsPlaylists { get; init; } = true;
    public bool FollowRedirects { get; init; } = true;

    /// <summary>Null means no timeout, which is what a live stream needs.</summary>
    public TimeSpan? Timeout { get; init; }
}
