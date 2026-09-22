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

/// <summary>
/// One entry from a channel guide.
/// <para>
/// A recording arrives as a file named after a channel and a timestamp, which
/// no metadata provider can match. What the guide said was on is the only thing
/// that identifies it, so it travels with the import rather than being guessed
/// at afterwards.
/// </para>
/// </summary>
public sealed record PluginEpgProgram
{
    public required string ChannelId { get; init; }

    public required string Title { get; init; }

    public required DateTimeOffset Start { get; init; }

    public required DateTimeOffset Stop { get; init; }

    public string? Subtitle { get; init; }

    public string? Description { get; init; }

    /// <summary>Season and episode when the guide carried them, which is rare outside drama.</summary>
    public int? Season { get; init; }

    public int? Episode { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>The provider's own rating string, kept verbatim because every
    /// country words it differently and parental controls compare it to what the
    /// owner configured, not to a scale we invented.</summary>
    public string? AgeRating { get; init; }

    public Uri? IconUrl { get; init; }

    /// <summary>False unless the guide said so: a repeat marked new is worse
    /// than one marked nothing.</summary>
    public bool IsNew { get; init; }
}
