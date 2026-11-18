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
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Commands;
using NoMercy.Storage;

namespace NoMercy.Encoder.Pipeline.Resume;

/// <summary>
/// Puts the entries before the resume point back into each playlist; EXTINF is the first-packet gap.
/// </summary>
public class EncodeResumeStitcher(
    IStorage storage,
    ISegmentProber prober,
    ILogger<EncodeResumeStitcher> logger
)
{
    internal const string StitchSuffix = ".stitch";
    private const string ExtInfTag = "#EXTINF:";
    private const string MediaSequenceTag = "#EXT-X-MEDIA-SEQUENCE:";
    private const string TargetDurationTag = "#EXT-X-TARGETDURATION:";
    private const int ProbeParallelism = 4;

    /// <returns>Null when nothing was left to stitch or every playlist was stitched; otherwise the reason it failed, with every playlist as it was.</returns>
    public async Task<string?> StitchAsync(
        FfmpegCommand[] commands,
        string outputDirectory,
        string correlationId,
        CancellationToken ct
    )
    {
        FfmpegCommand? main = commands.FirstOrDefault(c => c.ResumeMarkerPath is not null);
        if (main is null)
            return null;

        try
        {
            int start = StartNumber(main.Arguments);
            List<(string TempPath, string Path, string Text)> stitched = [];
            foreach (
                EncodeResumeResolver.HlsOutput output in EncodeResumeResolver.ParseOutputs([main])
            )
            {
                if (output.Playlist is null)
                    continue;

                string playlistPath = storage.CombinePath(outputDirectory, output.Playlist);
                string text = await StitchPlaylistAsync(
                    output,
                    playlistPath,
                    outputDirectory,
                    start,
                    ct
                );
                stitched.Add((playlistPath + StitchSuffix, playlistPath, text));
            }

            foreach ((string tempPath, string path, string text) in stitched)
            {
                await storage.WriteAllTextAsync(tempPath, text, ct);
                await storage.DeleteAsync(path, ct);
                await storage.MoveAsync(tempPath, path, ct);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "[{CorrelationId}] Playlists of the resumed encode were not stitched: {Reason}",
                correlationId,
                ex.Message
            );
            return $"The playlists of the resumed encode could not be stitched: {ex.Message}";
        }
    }

    private async Task<string> StitchPlaylistAsync(
        EncodeResumeResolver.HlsOutput output,
        string playlistPath,
        string outputDirectory,
        int start,
        CancellationToken ct
    )
    {
        if (!await storage.ExistsAsync(playlistPath, ct))
            throw new InvalidOperationException($"playlist {output.Playlist} is missing");

        string[] lines = (await storage.ReadAllTextAsync(playlistPath, ct)).Split('\n');
        int firstEntry = Array.FindIndex(
            lines,
            l => l.StartsWith(ExtInfTag, StringComparison.Ordinal)
        );
        string? firstSegment = firstEntry < 0 ? null : lines.Skip(firstEntry + 1).FirstOrDefault();
        string expectedName = Path.GetFileName(output.SegmentFile(start));
        if (firstSegment?.EndsWith(expectedName, StringComparison.Ordinal) != true)
            throw new InvalidOperationException(
                $"playlist {output.Playlist} does not start at segment {expectedName}"
            );

        // ffmpeg's own spelling of the segment URI (a bare name or a relative path).
        string uriPrefix = firstSegment[..^expectedName.Length];

        double?[] starts = new double?[start + 1];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, start + 1),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ProbeParallelism,
                CancellationToken = ct,
            },
            async (index, token) =>
                starts[index] = await prober.FirstPtsAsync(
                    storage.CombinePath(outputDirectory, output.SegmentFile(index)),
                    token
                )
        );

        int missing = Array.FindIndex(starts, s => s is null);
        if (missing >= 0)
            throw new InvalidOperationException(
                $"segment {Path.GetFileName(output.SegmentFile(missing))} could not be probed"
            );

        double longest = lines
            .Where(l => l.StartsWith(ExtInfTag, StringComparison.Ordinal))
            .Select(ParseDuration)
            .DefaultIfEmpty(0)
            .Max();
        StringBuilder rebuilt = new();
        for (int i = 0; i < start; i++)
        {
            double duration = starts[i + 1]!.Value - starts[i]!.Value;
            longest = Math.Max(longest, duration);
            rebuilt
                .Append(ExtInfTag)
                .Append(duration.ToString("F6", CultureInfo.InvariantCulture))
                .Append(",\n")
                .Append(uriPrefix)
                .Append(Path.GetFileName(output.SegmentFile(i)))
                .Append('\n');
        }

        IEnumerable<string> header = lines[..firstEntry]
            .Select(l =>
                l.StartsWith(MediaSequenceTag, StringComparison.Ordinal) ? MediaSequenceTag + "0"
                : l.StartsWith(TargetDurationTag, StringComparison.Ordinal)
                    ? TargetDurationTag
                        + ((int)Math.Round(longest, MidpointRounding.AwayFromZero)).ToString(
                            CultureInfo.InvariantCulture
                        )
                : l
            );

        return string.Join('\n', header) + "\n" + rebuilt + string.Join('\n', lines[firstEntry..]);
    }

    private static int StartNumber(string[] arguments)
    {
        int index = Array.IndexOf(arguments, "-start_number");
        if (
            index < 0
            || index + 1 >= arguments.Length
            || !int.TryParse(
                arguments[index + 1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int start
            )
            || start < 1
        )
            throw new InvalidOperationException("the resumed command has no start segment");

        return start;
    }

    private static double ParseDuration(string extInfLine)
    {
        string value = extInfLine[ExtInfTag.Length..].Split(',')[0];
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double duration
        )
            ? duration
            : 0;
    }
}
