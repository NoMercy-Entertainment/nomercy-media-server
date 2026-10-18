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
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Drives;
using NoMercy.Storage;

namespace NoMercy.OpticalMedia.Sources.AudioCd;

/// <summary>
/// CD reader covering both audio and data CDs. Audio CDs are probed via
/// nomercy-ffmpeg's <c>libcdio</c> input device — each CD-DA track shows
/// up as an audio stream. If libcdio reports zero streams (data CD with
/// a filesystem instead of CD-DA tracks), falls back to enumerating the
/// disc's top-level files via the storage driver.
///
/// Single source for <see cref="OpticalDiscType.Cd"/> because the enum
/// doesn't distinguish audio vs data — the disc itself decides which
/// branch fires.
/// </summary>
public sealed class CdDiscSource(
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorageDriver storageDriver,
    ILogger<CdDiscSource> logger
) : IDiscSource
{
    public OpticalDiscType Type => OpticalDiscType.Cd;

    public async Task<DiscInfo> ProbeAsync(DiscDrive drive, CancellationToken ct)
    {
        DiscTrack[] audioTracks = await ProbeAudioTracksAsync(drive, ct);
        if (audioTracks.Length > 0)
        {
            DiscTitle[] titles = audioTracks
                .Select(t => new DiscTitle(
                    Index: t.Index,
                    Name: $"Track {t.Index:D2}",
                    Duration: t.Duration,
                    VideoStreams: [],
                    AudioStreams: [],
                    Subtitles: [],
                    Chapters: [],
                    EstimatedSizeBytes: 0,
                    IsMainFeature: false
                ))
                .ToArray();

            return new(
                Type: OpticalDiscType.Cd,
                DiscLabel: drive.Label,
                Titles: titles,
                AudioTracks: audioTracks,
                TotalDuration: audioTracks.Sum(t => t.Duration.Ticks) is long ticks
                    ? TimeSpan.FromTicks(ticks)
                    : TimeSpan.Zero,
                DrivePath: drive.Path
            );
        }

        // Data CD fallback: enumerate top-level files via the unvalidated
        // low-level driver (disc roots aren't in the library allowlist).
        DiscTrack[] files = EnumerateDataCd(drive);
        return new(
            Type: OpticalDiscType.Cd,
            DiscLabel: drive.Label,
            Titles: [],
            AudioTracks: files,
            TotalDuration: TimeSpan.Zero,
            DrivePath: drive.Path
        );
    }

    public Task<DiscTitle> ProbeTitleAsync(DiscDrive drive, int titleIndex, CancellationToken ct)
    {
        // Per-track / per-file detail isn't fetched separately — everything
        // we know is in the initial probe.
        return Task.FromResult(
            new DiscTitle(
                Index: titleIndex,
                Name: $"Track {titleIndex:D2}",
                Duration: TimeSpan.Zero,
                VideoStreams: [],
                AudioStreams: [],
                Subtitles: [],
                Chapters: [],
                EstimatedSizeBytes: 0,
                IsMainFeature: false
            )
        );
    }

    private async Task<DiscTrack[]> ProbeAudioTracksAsync(DiscDrive drive, CancellationToken ct)
    {
        ProcessResult result = await processRunner.RunAsync(
            options.FfprobePath,
            [
                "-hide_banner",
                "-v",
                "quiet",
                "-f",
                "libcdio",
                "-print_format",
                "json",
                "-show_format",
                "-show_streams",
                "-i",
                drive.Path,
            ],
            workingDirectory: null,
            cancellationToken: ct
        );

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StdOut))
            return [];

        try
        {
            return ParseTracks(result.StdOut);
        }
        catch (JsonException ex)
        {
            logger.LogInformation(
                ex,
                "Audio CD probe parse failed for {Drive}: {Message}",
                drive.Path,
                ex.Message
            );
            return [];
        }
    }

    private DiscTrack[] EnumerateDataCd(DiscDrive drive)
    {
        try
        {
            // Derive separator from the drive path itself so the path is
            // valid on both Windows and Linux regardless of the host.
            char sep = drive.Path.Contains('\\') ? '\\' : '/';
            string root = drive.Path.TrimEnd('\\', '/') + sep;
            int idx = 1;
            List<DiscTrack> tracks = new();
            foreach (
                string entry in storageDriver.EnumerateFileSystemEntries(
                    root,
                    "*",
                    SearchOption.TopDirectoryOnly
                )
            )
            {
                if (storageDriver.DirectoryExists(entry))
                    continue;
                tracks.Add(
                    new(
                        Index: idx++,
                        // Separator-agnostic leaf: Path.GetFileName treats '\' as
                        // a literal on Linux, so a Windows-style entry would keep
                        // its whole path on the CI runner. Split on both separators.
                        Title: entry[(entry.LastIndexOfAny(['\\', '/']) + 1)..],
                        Artist: null,
                        Duration: TimeSpan.Zero,
                        SampleRate: 0,
                        Channels: 0
                    )
                );
            }
            return tracks.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                ex,
                "Data CD enumeration failed for {Drive}: {Message}",
                drive.Path,
                ex.Message
            );
            return [];
        }
    }

    /// <summary>
    /// libcdio reports each CD-DA track as an audio stream in ffprobe's
    /// JSON output. Track index = stream <c>index</c> field + 1.
    /// </summary>
    internal static DiscTrack[] ParseTracks(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams))
            return [];

        List<DiscTrack> result = new();
        foreach (JsonElement stream in streams.EnumerateArray())
        {
            string codecType = stream.TryGetProperty("codec_type", out JsonElement ct)
                ? ct.GetString() ?? ""
                : "";
            if (!string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
                continue;

            int rawIndex = stream.TryGetProperty("index", out JsonElement i) ? i.GetInt32() : 0;

            TimeSpan duration = TimeSpan.Zero;
            if (
                stream.TryGetProperty("duration", out JsonElement d)
                && double.TryParse(
                    d.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double seconds
                )
            )
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            int sampleRate =
                stream.TryGetProperty("sample_rate", out JsonElement sr)
                && int.TryParse(sr.GetString(), out int srInt)
                    ? srInt
                    : 44100;
            int channels = stream.TryGetProperty("channels", out JsonElement ch)
                ? ch.GetInt32()
                : 2;

            result.Add(
                new(
                    Index: rawIndex + 1,
                    Title: null,
                    Artist: null,
                    Duration: duration,
                    SampleRate: sampleRate,
                    Channels: channels
                )
            );
        }

        return result.ToArray();
    }
}
