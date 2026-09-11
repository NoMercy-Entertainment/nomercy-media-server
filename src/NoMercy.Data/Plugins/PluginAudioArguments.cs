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

using NoMercy.NmSystem.Information;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The exact ffmpeg command lines <see cref="PluginAudioTools" /> runs, kept
/// apart from the decisions around them. Every argument here is asserted on
/// in the tests: a plugin's filter graph is the one thing the host cannot
/// re-derive from the outcome, so the command has to be readable on its own
/// rather than assembled in the middle of a method that also resolves paths,
/// takes a lock and talks to the database.
/// </summary>
internal static class PluginAudioArguments
{
    /// <summary>What a plugin writes in its filter text where the model file's name belongs.</summary>
    private const string ModelToken = "{stemsModel}";

    /// <summary>
    /// The model file's own name, not its path: ffmpeg runs with
    /// <see cref="AppFiles.FfmpegFolder" /> as its working directory, so a
    /// bare name resolves there on every machine.
    /// </summary>
    internal static string ModelFileName => AppFiles.StemsplitModel + ".gguf";

    /// <summary>Substitutes the model file's name for <c>{stemsModel}</c>.</summary>
    internal static string ApplyModelToken(string text) =>
        text.Replace(ModelToken, ModelFileName, StringComparison.Ordinal);

    /// <summary>
    /// One filter chain over one input, decoded and thrown away: the plugin
    /// is after what the filters print, not after a file.
    /// <para>
    /// A complex graph also maps the input to the null muxer, because a
    /// <c>-filter_complex</c> whose output pads nothing consumes makes ffmpeg
    /// refuse to run at all.
    /// </para>
    /// </summary>
    internal static string[] FilterGraph(string inputPath, string text, bool complex) =>
        complex
            ?
            [
                "-nostdin",
                "-i",
                inputPath,
                "-vn",
                "-sn",
                "-dn",
                "-filter_complex",
                text,
                "-map",
                "0:a",
                "-f",
                "null",
                "-",
            ]
            : ["-nostdin", "-i", inputPath, "-vn", "-sn", "-dn", "-af", text, "-f", "null", "-"];

    /// <summary>
    /// Spleeter 2 stems in one pass: the filter has two output pads, vocals
    /// first, and each is encoded to its own Opus file. 160 kbit/s at 48 kHz
    /// because a stem is an intermediate a transition is rendered from, not
    /// something a listener ever hears on its own.
    /// </summary>
    /// <param name="windowArguments">Empty for full coverage; otherwise the <c>-t</c> or <c>-ss</c> pair.</param>
    internal static string[] StemSplit(
        string inputPath,
        IReadOnlyList<string> windowArguments,
        string vocalsPath,
        string accompanimentPath
    ) =>
        [
            "-nostdin",
            "-i",
            inputPath,
            .. windowArguments,
            "-vn",
            "-sn",
            "-dn",
            "-filter_complex",
            $"[0:a]stemsplit=model={ModelFileName}[voc][acc]",
            "-map",
            "[voc]",
            "-c:a",
            "libopus",
            "-b:a",
            "160k",
            "-ar",
            "48000",
            vocalsPath,
            "-map",
            "[acc]",
            "-c:a",
            "libopus",
            "-b:a",
            "160k",
            "-ar",
            "48000",
            accompanimentPath,
        ];

    /// <summary>
    /// The build token out of ffmpeg's own banner - the first line it writes
    /// to stderr, <c>ffmpeg version 9.0-NoMercy-MediaServer …</c>. It goes
    /// into every stem's producer version, so a model or binary upgrade can
    /// be spotted the way an analyzer upgrade is; "unknown" when the banner
    /// was not what it usually is, which is still better than no marker.
    /// </summary>
    internal static string FfmpegVersion(string? bannerLine)
    {
        const string prefix = "ffmpeg version ";

        if (bannerLine is null || !bannerLine.StartsWith(prefix, StringComparison.Ordinal))
        {
            return "unknown";
        }

        string token = bannerLine[prefix.Length..].Split(' ')[0].Trim();
        return token.Length == 0 ? "unknown" : token;
    }
}
