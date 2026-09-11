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
/// The one place that knows the on-disk shape of those columns.
/// <c>NoMercy.Data.Plugins.PluginMusicQuery</c> (what a plugin reads) and
/// <c>NoMercy.Api.DTOs.Music.TrackDjAnalysisDto</c> (what the API returns) both
/// call this instead of deserializing the text themselves, so the two surfaces
/// can never quietly drift onto different shapes.
/// </para>
/// </summary>
internal static class DjAnalysisJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Null when <paramref name="json" /> is null, blank, or fails to parse —
    /// the caller decides what an absent or malformed column means to it (log
    /// it, default to empty, or something else). A valid but empty array still
    /// comes back as an empty, non-null list.
    /// </summary>
    public static List<T>? TryDeserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The shape one entry of <c>cue_points</c> takes on disk: <c>{ ms, type, direction, score }</c>.</summary>
    internal sealed record CuePointRow(int Ms, string? Type, string? Direction, double Score);

    /// <summary>The shape one entry of <c>chords</c> takes on disk: <c>{ ms, chord }</c>.</summary>
    internal sealed record ChordRow(int Ms, string? Chord);
}
