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
using System.Text.RegularExpressions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Reads the stemsplit filter's own lines in ffmpeg's log. The text is the
/// filter's, as stemsplit 2.0.1 (nomercy-ffmpeg v1.0.47) prints it, read off
/// the real binary; ffmpeg puts the filter's name and address in front
/// (<c>[Parsed_stemsplit_0 @ 000001ae15ff8a00] stemsplit: …</c>), and without
/// <c>-nostats</c> a line can follow a progress report on the same line, so
/// every match looks inside the line instead of at its start.
/// </summary>
internal static partial class StemsplitLog
{
    /// <summary>
    /// What both of the filter's RoFormer refusals start with: the one at
    /// filter creation (<c>… and no GPU of this machine can run RoFormer; …</c>)
    /// and the one when the first audio arrives (<c>… and 2 of the run's 4
    /// model(s) would run on the CPU; …</c>). <c>cpu_fallback=0 needs
    /// use_gpu=auto …</c> is a mistake in the options, not a verdict, and does
    /// not match.
    /// </summary>
    internal const string RefusalPrefix = "stemsplit: cpu_fallback=0 and ";

    private const string NoGpuAtAll = "no GPU of this machine can run RoFormer";

    /// <summary>
    /// Whether the line is the filter's verdict that RoFormer cannot run on
    /// this machine's GPU. A build older than stemsplit 2.0 does not know
    /// <c>cpu_fallback</c> and ffmpeg refuses the graph instead; that build
    /// cannot run the host's RoFormer either, so it counts the same.
    /// </summary>
    internal static bool IsRefusal(string line) =>
        line.Contains(RefusalPrefix, StringComparison.Ordinal) || UnknownOption().IsMatch(line);

    /// <summary>
    /// Whether the refusal is about the machine rather than about the run: no
    /// GPU at all, or a filter that cannot be asked. Every preset is refused
    /// then, not only the one this run asked for.
    /// </summary>
    internal static bool RefusesEveryPreset(string line) =>
        line.Contains(NoGpuAtAll, StringComparison.Ordinal) || UnknownOption().IsMatch(line);

    /// <summary>
    /// The model count of a run the filter refused for its memory, from
    /// <c>… and 2 of the run's 4 model(s) would run on the CPU; …</c>. Null for
    /// any other line.
    /// </summary>
    internal static int? RefusedRunModels(string line)
    {
        Match match = ModelsOnTheCpu().Match(line);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// One of RoFormer's per-chunk lines,
    /// <c>stemsplit: model 1/2, chunk 9/9 at sample 1719900 (485100 samples, 2205000 received), 38.9 s elapsed, rtf 1.27.</c>,
    /// or null. The chunk count is <c>?</c> until the filter knows where the
    /// input ends. Spleeter prints none of these.
    /// </summary>
    internal static StemsplitChunk? Chunk(string line)
    {
        Match match = ChunkLine().Match(line);
        if (!match.Success)
            return null;

        return new(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            match.Groups[4].Value == "?"
                ? null
                : long.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture)
        );
    }

    /// <summary>
    /// The backend and device RoFormer runs on, from
    /// <c>stemsplit: RoFormer runs on 'vulkan' (NVIDIA GeForce RTX 3070).</c>,
    /// as <c>vulkan (NVIDIA GeForce RTX 3070)</c>; null for any other line.
    /// </summary>
    internal static string? RoformerDevice(string line)
    {
        Match match = RunsOn().Match(line);
        return match.Success ? $"{match.Groups[1].Value} ({match.Groups[2].Value})" : null;
    }

    [GeneratedRegex(
        @"Error applying option '(?:cpu_fallback|preset)' to filter 'stemsplit'",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex UnknownOption();

    [GeneratedRegex(
        @"stemsplit: cpu_fallback=0 and \d+ of the run's (\d+) model\(s\) would run on the CPU",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ModelsOnTheCpu();

    [GeneratedRegex(
        @"stemsplit: model (\d+)/(\d+), chunk (\d+)/(\d+|\?) at sample ",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ChunkLine();

    [GeneratedRegex(
        @"stemsplit: RoFormer runs on '([^']*)' \((.*)\)\.",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex RunsOn();
}
