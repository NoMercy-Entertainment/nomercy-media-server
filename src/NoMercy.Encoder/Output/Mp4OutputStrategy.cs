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
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Pipeline;
using NoMercy.Storage;

namespace NoMercy.Encoder.Output;

public class Mp4OutputStrategy(IStorage storage, IFfmpegExecutor? ffmpegExecutor = null)
    : IOutputStrategy
{
    public OutputFormat Format => OutputFormat.Mp4;

    public void ConfigureOutput(
        FfmpegCommandBuilder builder,
        OutputPlan plan,
        string outputDirectory
    )
    {
        string outputPath = Path.Combine(outputDirectory, "output.mp4");
        List<string> mapStreams = [];

        foreach (VideoOutputPlan video in plan.VideoOutputs)
            mapStreams.Add(video.MapLabel);

        foreach (AudioOutputPlan audio in plan.AudioOutputs)
            if (audio.Action is StreamAction.Copy or StreamAction.Transcode)
                mapStreams.Add(audio.MapLabel);

        VideoOutputPlan? primaryVideo = plan.VideoOutputs.Length > 0 ? plan.VideoOutputs[0] : null;
        AudioOutputPlan? primaryAudio = plan.AudioOutputs.Length > 0 ? plan.AudioOutputs[0] : null;

        Dictionary<string, string> extraFlags = new() { ["-movflags"] = "+faststart" };

        // Dolby Vision HEVC must be tagged dvh1 in MP4 so players recognize
        // the DV profile. Without this, QuickTime/Apple TV play as HDR10.
        if (plan.PreserveDolbyVision && primaryVideo is not null)
        {
            extraFlags["-tag:v"] = "dvh1";
        }

        if (
            primaryAudio?.Action == StreamAction.Transcode
            && !string.IsNullOrEmpty(primaryAudio.AudioFilter)
        )
        {
            extraFlags["-af"] = primaryAudio.AudioFilter;
        }

        builder.AddOutput(
            new(
                FilePath: outputPath,
                VideoCodec: primaryVideo?.EncoderName,
                AudioCodec: primaryAudio?.Action == StreamAction.Copy
                    ? "copy"
                    : primaryAudio?.EncoderName,
                Crf: primaryVideo is { Crf: > 0 } ? primaryVideo.Crf : null,
                VideoBitrateKbps: primaryVideo is { BitrateKbps: > 0 }
                    ? primaryVideo.BitrateKbps
                    : null,
                Preset: primaryVideo?.Preset,
                Profile: primaryVideo?.Profile,
                PixelFormat: primaryVideo is { TenBit: true } ? primaryVideo.PixelFormat : null,
                AudioBitrateKbps: primaryAudio?.Action == StreamAction.Transcode
                    ? primaryAudio.BitrateKbps
                    : null,
                MapStreams: mapStreams.ToArray(),
                ExtraFlags: extraFlags
            )
        );
    }

    public async Task FinalizeAsync(
        string outputDirectory,
        OutputPlan plan,
        string mediaTitle,
        CancellationToken ct
    )
    {
        // Rename the generic output.mp4 to the proper media title. Audio-only
        // encodes land as .m4a (the convention music players expect);
        // video-bearing encodes stay .mp4.
        string extension = plan.VideoOutputs.Length == 0 ? ".m4a" : ".mp4";
        string sourcePath = Path.Combine(outputDirectory, "output.mp4");
        string targetPath = Path.Combine(outputDirectory, $"{mediaTitle}{extension}");

        if (storage.Exists(sourcePath) && sourcePath != targetPath)
        {
            storage.Delete(targetPath);
            storage.Move(sourcePath, targetPath);
        }

        // Embed chapter metadata when chapters are present and an executor is available.
        if (plan.Chapters is { Count: > 0 } chapters && ffmpegExecutor is not null)
        {
            await EmbedChaptersAsync(outputDirectory, targetPath, chapters, ct);
        }
    }

    /// <summary>
    /// Writes a .chapters.ffmeta file and re-muxes the MP4 to embed chapter
    /// metadata via ffmpeg's ffmetadata input.
    /// </summary>
    private async Task EmbedChaptersAsync(
        string outputDirectory,
        string mp4Path,
        IReadOnlyList<ChapterInfo> chapters,
        CancellationToken ct
    )
    {
        string ffmetaPath = Path.Combine(outputDirectory, ".chapters.ffmeta");
        string tempPath = mp4Path + ".chapters.tmp.mp4";

        // Build ffmetadata content
        StringBuilder sb = new();
        sb.AppendLine(";FFMETADATA1");
        sb.AppendLine();

        for (int i = 0; i < chapters.Count; i++)
        {
            ChapterInfo chapter = chapters[i];
            int startMs = (int)chapter.Start.TotalMilliseconds;
            int endMs =
                i + 1 < chapters.Count
                    ? (int)chapters[i + 1].Start.TotalMilliseconds
                    : (int)chapter.End.TotalMilliseconds;

            sb.AppendLine("[CHAPTER]");
            sb.AppendLine("TIMEBASE=1/1000");
            sb.AppendLine($"START={startMs}");
            sb.AppendLine($"END={endMs}");
            sb.AppendLine($"title={chapter.Title ?? $"Chapter {i + 1}"}");
            sb.AppendLine();
        }

        await storage.WriteAsync(ffmetaPath, Encoding.UTF8.GetBytes(sb.ToString()), ct);

        // Re-mux: copy all streams + inject chapter metadata from ffmeta file
        FfmpegCommand remuxCmd = new(
            Executable: "ffmpeg",
            Arguments:
            [
                "-y",
                "-i",
                mp4Path,
                "-i",
                ffmetaPath,
                "-map",
                "0",
                "-map_metadata",
                "1",
                "-metadata_header_padding",
                "1024",
                "-c",
                "copy",
                tempPath,
            ],
            WorkingDirectory: outputDirectory
        );

        await ffmpegExecutor!.ExecuteAsync(remuxCmd, TimeSpan.Zero, ct: ct);

        // Swap temp → final
        if (storage.Exists(tempPath))
        {
            storage.Delete(mp4Path);
            storage.Move(tempPath, mp4Path);
        }

        // Clean up ffmeta sidecar
        if (storage.Exists(ffmetaPath))
            storage.Delete(ffmetaPath);
    }

    public string[] GetOutputSubdirectories(OutputPlan plan) => [];
}
