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

using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.Encoder.Trailers;

/// <summary>
/// Builds the yt-dlp | ffmpeg shell command used to fetch and remux a trailer.
/// The command string is executed through a shell (<c>cmd /c</c> / <c>bash -c</c>),
/// so every interpolated value is a trust boundary. The trailer id is validated by
/// the caller (<c>HomeController.TrailerIdRegex</c>); the subtitle language comes from
/// the client's <c>Accept-Language</c> header and is validated here — anything that is
/// not a plain BCP-47-shaped locale token is dropped rather than passed to the shell,
/// closing the command-injection vector.
/// </summary>
public static partial class TrailerCommandBuilder
{
    // Locale tokens only: "en", "en-US", "pt-BR", "zh-Hans". No quotes, spaces,
    // shell metacharacters, path separators, or length that could break out of the
    // quoted "-o subtitle:..." argument.
    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8}){0,3}$")]
    private static partial Regex SafeLanguageRegex();

    public static bool IsSafeLanguage(string? language) =>
        !string.IsNullOrEmpty(language) && SafeLanguageRegex().IsMatch(language);

    /// <summary>
    /// Builds the inner command string (without the shell wrapper). A subtitle
    /// language argument is only included when <paramref name="language"/> is a safe
    /// locale token; an unsafe value is silently ignored so the trailer still fetches.
    /// </summary>
    public static string Build(
        string ytdlpPath,
        string ffmpegPath,
        string trailerId,
        string? language
    )
    {
        StringBuilder sb = new();

        sb.Append(ytdlpPath);
        sb.Append(" -f bestvideo+bestaudio  --extractor-args \"youtube:player_client=default\" ");

        if (IsSafeLanguage(language))
            sb.Append($" -o \"subtitle:{language}.%(ext)s\" --sub-langs all --write-subs ");

        sb.Append(trailerId);

        sb.Append(" -o - ");
        sb.Append(
            $" | {ffmpegPath} -i pipe: -map 0:0 -map 0:1 -c:v libx264 -c:a aac -ac 2 -preset ultrafast "
        );
        sb.Append(
            "-segment_list_type m3u8 -hls_playlist_type event -hls_init_time 4 -hls_time 4 -hls_segment_filename video_%05d.ts video.m3u8 "
        );

        return sb.ToString();
    }
}
