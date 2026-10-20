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
/// The codes an <see cref="IPluginLyrics" /> refusal starts with, followed by
/// a colon and the reason in words. A plugin branches on the code; the owner
/// reads the words. Added in ABI 12.6.
/// </summary>
public static class PluginLyricsRefusal
{
    /// <summary>The version moved since the plugin read it.</summary>
    public const string VersionConflict = "version_conflict";

    /// <summary>The track has synced provider lyrics, which a plugin never overwrites.</summary>
    public const string ProviderSynced = "provider_synced";

    /// <summary>No lines, lines out of order, a time outside the track, or words that disagree with their line.</summary>
    public const string InvalidLines = "invalid_lines";

    /// <summary>The source is not one the host knows.</summary>
    public const string InvalidSource = "invalid_source";

    /// <summary>Clearing is only for generated lyrics.</summary>
    public const string NotGenerated = "not_generated";

    /// <summary>The track does not exist.</summary>
    public const string UnknownTrack = "unknown_track";
}
