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

namespace NoMercy.MediaProcessing.AudioAnalysis;

/// <summary>
/// Translates the key names the detector emits into Camelot notation.
/// <para>
/// The detector names keys with sharps and an "m" suffix — "C", "F#", "Am" —
/// which is twenty-four possible values and nothing else. Camelot numbers put
/// harmonically adjacent keys next to each other, which is what makes any
/// key-matching rule a comparison instead of a music theory exercise.
/// </para>
/// </summary>
public static class CamelotKey
{
    private static readonly IReadOnlyDictionary<string, string> Wheel = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["C"] = "8B",
        ["C#"] = "3B",
        ["D"] = "10B",
        ["D#"] = "5B",
        ["E"] = "12B",
        ["F"] = "7B",
        ["F#"] = "2B",
        ["G"] = "9B",
        ["G#"] = "4B",
        ["A"] = "11B",
        ["A#"] = "6B",
        ["B"] = "1B",
        ["Cm"] = "5A",
        ["C#m"] = "12A",
        ["Dm"] = "7A",
        ["D#m"] = "2A",
        ["Em"] = "9A",
        ["Fm"] = "4A",
        ["F#m"] = "11A",
        ["Gm"] = "6A",
        ["G#m"] = "1A",
        ["Am"] = "8A",
        ["A#m"] = "3A",
        ["Bm"] = "10A",
    };

    /// <summary>
    /// The Camelot code for a detector key name, or null when the name is not
    /// one the detector can produce. Null rather than a guess: a wrong Camelot
    /// code silently mismatches tracks, and an absent one only skips them.
    /// </summary>
    public static string? FromKeyName(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return null;
        }

        return Wheel.GetValueOrDefault(keyName.Trim());
    }

    private static readonly IReadOnlyDictionary<string, int> PitchClasses = new Dictionary<
        string,
        int
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["C"] = 0,
        ["B#"] = 0,
        ["C#"] = 1,
        ["Db"] = 1,
        ["D"] = 2,
        ["D#"] = 3,
        ["Eb"] = 3,
        ["E"] = 4,
        ["Fb"] = 4,
        ["F"] = 5,
        ["E#"] = 5,
        ["F#"] = 6,
        ["Gb"] = 6,
        ["G"] = 7,
        ["G#"] = 8,
        ["Ab"] = 8,
        ["A"] = 9,
        ["A#"] = 10,
        ["Bb"] = 10,
        ["B"] = 11,
        ["Cb"] = 11,
    };

    /// <summary>
    /// The pitch class (0 = C .. 11 = B) of a detector key name's tonic, or
    /// null when the name is empty or its tonic is not one of the twelve
    /// notes (sharp or flat spelling, either is accepted). A minor key is
    /// keyed by its tonic — "Am" is 9, the same as "A" — because automix
    /// key-matching cares which note two tracks share, not whether either is
    /// major or minor.
    /// </summary>
    public static int? PitchClassFromKeyName(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return null;
        }

        string trimmed = keyName.Trim();
        string tonic = trimmed.Length > 1 && trimmed.EndsWith('m') ? trimmed[..^1] : trimmed;

        return PitchClasses.TryGetValue(tonic, out int pitchClass) ? pitchClass : null;
    }
}
