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

using System.Text.RegularExpressions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// What a filter graph's text says about the stemsplit filters in it. A
/// filter's name stands at the start of the text or after a <c>,</c>, a
/// <c>;</c> or a pad label's <c>]</c>, and its options follow an <c>=</c> up to
/// the next <c>,</c>, <c>;</c> or <c>[</c>.
/// </summary>
internal static partial class StemsplitGraph
{
    // The option names that make a stemsplit filter run RoFormer instead of
    // Spleeter (stemsplit 2.0.1, `ffmpeg -h filter=stemsplit`).
    private static readonly string[] RoformerOptions =
    [
        "preset",
        "models",
        "vocals_models",
        "accompaniment_models",
    ];

    /// <summary>
    /// Whether the graph has a <c>whisper</c> filter. RoFormer switches ggml's
    /// Vulkan backend to full precision for the whole ffmpeg process, which
    /// makes whisper on the GPU about 3.5 times slower, so a graph that
    /// transcribes is given Spleeter.
    /// </summary>
    internal static bool HasWhisper(string graphText) => WhisperFilter().IsMatch(graphText);

    /// <summary>
    /// Whether any stemsplit filter of the (resolved) graph runs RoFormer: a
    /// preset or a model list in its options. Such a run hands its audio on a
    /// whole chunk at a time, after loading models for up to a minute.
    /// </summary>
    internal static bool RunsRoformer(string graphText)
    {
        foreach (Match filter in StemsplitFilter().Matches(graphText))
        {
            foreach (string option in filter.Groups[1].Value.Split(':'))
            {
                string name = option.Split('=', 2)[0].Trim();
                if (RoformerOptions.Contains(name, StringComparer.Ordinal))
                    return true;
            }
        }

        return false;
    }

    [GeneratedRegex(
        @"(?:^|[,;\]])\s*whisper\s*(?:=|[,;\[]|$)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex WhisperFilter();

    [GeneratedRegex(
        @"(?:^|[,;\]])\s*stemsplit\s*=([^,;\[]*)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex StemsplitFilter();
}
