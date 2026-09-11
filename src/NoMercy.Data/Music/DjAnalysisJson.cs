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

using System.Text.Json;

namespace NoMercy.Data.Music;

/// <summary>
/// Parses the JSON text columns on <c>TrackDjAnalysis</c> — <c>phrase_starts_ms</c>,
/// <c>vocal_regions_ms</c>, <c>bar_energy</c>, <c>cue_points</c>, <c>chords</c>.
/// <para>
/// The one parser for those columns' on-disk shape, shared by the two
/// surfaces that read them: <c>NoMercy.Data.Plugins.PluginMusicQuery</c>
/// (what a plugin reads) and <c>NoMercy.Api.DTOs.Music.TrackDjAnalysisDto</c>
/// (what the API returns) both call this instead of deserializing the text
/// themselves, so the two can never quietly drift onto different shapes.
/// Public rather than a friend-assembly internal: <c>NoMercy.Api</c> already
/// references <c>NoMercy.Data</c> for its repositories and entities, and a
/// project-wide <c>InternalsVisibleTo</c> would open every other internal in
/// this assembly to it just to reach this one helper.
/// </para>
/// </summary>
public static class DjAnalysisJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Null when <paramref name="json" /> is null, blank, or fails to parse —
    /// the caller decides what an absent or malformed column means to it (log
    /// it, default to empty, or something else). A valid but empty array still
    /// comes back as an empty, non-null list. Discards the parse error; a
    /// caller that wants it calls the <see cref="TryDeserialize{T}(string?, out Exception?)" />
    /// overload instead.
    /// </summary>
    public static List<T>? TryDeserialize<T>(string? json) => TryDeserialize<T>(json, out _);

    /// <summary>
    /// Same contract as <see cref="TryDeserialize{T}(string?)" />, plus the
    /// parse failure: <paramref name="error" /> is null when
    /// <paramref name="json" /> was null/blank (nothing was ever parsed) and
    /// the caught <see cref="JsonException" /> when it was present but
    /// malformed — the two absence reasons a caller like
    /// <c>PluginMusicQuery</c> logs differently.
    /// </summary>
    public static List<T>? TryDeserialize<T>(string? json, out Exception? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Options) ?? [];
        }
        catch (JsonException ex)
        {
            error = ex;
            return null;
        }
    }
}
