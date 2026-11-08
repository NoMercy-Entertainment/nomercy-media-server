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

namespace NoMercy.Encoder.Output;

/// <summary>
/// Checks a finished encode in its staging folder before it reaches the library.
/// </summary>
public static class EncodeOutputValidator
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".m4s", ".mp4", ".mkv", ".webm", ".mp3", ".aac", ".m4a", ".flac", ".opus", ".ogg",
    };

    /// <summary>Returns one line per problem; empty means the output is sound.</summary>
    public static IReadOnlyList<string> Validate(string directory)
    {
        List<string> problems = [];

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(directory, file).Replace('\\', '/');
            string extension = Path.GetExtension(file);

            if (extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                CheckPlaylist(file, rel, problems);
            else if (MediaExtensions.Contains(extension) && new FileInfo(file).Length == 0)
                problems.Add($"{rel} is empty");
        }

        return problems;
    }

    /// <summary>Flags video and audio renditions whose length differs from the source.</summary>
    public static IReadOnlyList<string> ValidateDurations(string directory, double expectedSeconds)
    {
        List<string> problems = [];
        if (expectedSeconds <= 0)
            return problems;

        double tolerance = Math.Max(5.0, expectedSeconds * 0.01);

        foreach (string playlist in Directory.EnumerateFiles(directory, "*.m3u8", SearchOption.AllDirectories))
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(playlist) ?? string.Empty);
            if (!folder.StartsWith("video_", StringComparison.OrdinalIgnoreCase)
                && !folder.StartsWith("audio_", StringComparison.OrdinalIgnoreCase))
                continue;

            double total = 0;
            bool any = false;
            foreach (string line in File.ReadLines(playlist))
            {
                if (!line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                    continue;
                string value = line["#EXTINF:".Length..].Split(',')[0];
                if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                {
                    total += seconds;
                    any = true;
                }
            }

            if (any && Math.Abs(total - expectedSeconds) > tolerance)
                problems.Add(
                    $"{Path.GetRelativePath(directory, playlist).Replace('\\', '/')} lasts {total:F1} s, the source lasts {expectedSeconds:F1} s");
        }

        return problems;
    }

    private static void CheckPlaylist(string file, string rel, List<string> problems)
    {
        string[] lines = File.ReadAllLines(file);
        if (lines.Length == 0 || !lines[0].StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            problems.Add($"{rel} is not a playlist (empty or missing #EXTM3U)");
            return;
        }

        string folder = Path.GetDirectoryName(file) ?? string.Empty;
        bool isMedia = lines.Any(l => l.StartsWith("#EXTINF", StringComparison.Ordinal));

        if (isMedia && !lines.Any(l => l.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal)))
            problems.Add($"{rel} has no #EXT-X-ENDLIST");

        foreach (string line in lines)
        {
            string uri = line.Trim();
            if (uri.Length == 0 || uri.StartsWith('#') || uri.Contains("://", StringComparison.Ordinal))
                continue;

            string target = Path.Combine(folder, uri.Split('?')[0]);
            if (!File.Exists(target))
                problems.Add($"{rel} names {uri}, which does not exist");
            else if (
                new FileInfo(target).Length == 0
                && !MediaExtensions.Contains(Path.GetExtension(target))
            )
                problems.Add($"{rel} names {uri}, which is empty");
        }
    }
}
