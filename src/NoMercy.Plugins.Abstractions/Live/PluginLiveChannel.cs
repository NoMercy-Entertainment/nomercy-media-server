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
/// A live channel the player plays natively.
/// The links are tried in order, so a provider that drops falls through instead
/// of ending playback, and the credentials in them are resolved on the server.
/// </summary>
public sealed record PluginLiveChannel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<PluginProxyLink> Links { get; init; }
    public required PluginLiveStreamKind StreamKind { get; init; }
    public int? Number { get; init; }
    public string? Group { get; init; }
    public Uri? Logo { get; init; }
    public bool Radio { get; init; }
    public bool Adult { get; init; }
    public string? AgeRating { get; init; }
    public PluginProviderLimits? ProviderLimits { get; init; }

    /// <summary>Null when the provider offers no catch-up for this channel.</summary>
    public Func<
        PluginCatchUpRequest,
        CancellationToken,
        Task<IReadOnlyList<PluginProxyLink>>
    >? CatchUpAsync { get; init; }
}
