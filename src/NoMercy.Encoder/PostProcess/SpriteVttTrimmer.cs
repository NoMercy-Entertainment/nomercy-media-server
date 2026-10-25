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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.Encoder.PostProcess;

/// <summary>
/// Drops the cues that point at padding tiles.
///
/// <para><see cref="SpriteGrid"/> fills the sheet's last row with black frames so
/// no cell is left for the muxer to render green. Those frames are real as far as
/// the muxer is concerned, so it writes a cue for each — with a timestamp past the
/// end of the film. A player that looks a frame up by time never reaches them, but
/// the TV seek strip renders every cue it is given, and would end on a run of
/// black tiles.</para>
///
/// <para>Trimming the cue list is what keeps that from being every client's
/// problem to know about. The tiles stay on the sheet, unreferenced.</para>
/// </summary>
public static partial class SpriteVttTrimmer
{
    [GeneratedRegex(
        @"^(?<h>\d+):(?<m>\d{2}):(?<s>\d{2})\.(?<ms>\d{3})\s*-->",
        RegexOptions.Compiled,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex CueStartRegex();

    /// <summary>
    /// Returns <paramref name="vtt"/> with every cue starting at or after
    /// <paramref name="duration"/> removed. Content that is not a cue — the
    /// WEBVTT header, blank lines — is preserved as written.
    /// </summary>
    public static string Trim(string vtt, TimeSpan duration)
    {
        string[] lines = vtt.Replace("\r\n", "\n").Split('\n');
        StringBuilder kept = new();

        // A cue is its timing line plus the payload lines under it, up to the
        // blank line that ends it. Deciding on the timing line and then carrying
        // that decision through the payload keeps the two from being separated.
        bool dropping = false;
        bool sawCue = false;

        foreach (string line in lines)
        {
            Match match = CueStartRegex().Match(line.Trim());

            if (match.Success)
            {
                sawCue = true;
                dropping = ReadStart(match) >= duration;
            }
            else if (line.Trim().Length == 0)
            {
                // Blank line closes the cue it followed; the next one decides again.
                if (dropping)
                {
                    dropping = false;
                    continue;
                }
            }

            if (!dropping)
                kept.Append(line).Append('\n');
        }

        // Nothing recognisable as a cue means this is not a shape worth rewriting.
        return sawCue ? kept.ToString().TrimEnd('\n') + "\n" : vtt;
    }

    private static TimeSpan ReadStart(Match match) =>
        new(
            0,
            int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture)
        );
}
