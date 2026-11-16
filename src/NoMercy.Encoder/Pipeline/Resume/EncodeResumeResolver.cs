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
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.BuildingBlocks.Drm;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.Profiles;
using NoMercy.Storage;

namespace NoMercy.Encoder.Pipeline.Resume;

/// <summary>
/// Finds where an interrupted HLS encode continues by counting its segment files.
/// Anything it cannot prove safe starts the encode over from zero.
/// </summary>
public class EncodeResumeResolver(
    IStorage storage,
    ISegmentProber prober,
    EncodeResumeMarker marker,
    ILogger<EncodeResumeResolver> logger
)
{
    private const string SegmentNumberToken = "_%05d";
    private const string EndListTag = "#EXT-X-ENDLIST";
    private const int AacFrameSamples = 1024;

    internal sealed record HlsOutput(
        string SegmentPrefix,
        string SegmentExtension,
        string? Playlist
    )
    {
        public string SegmentFile(int index) =>
            SegmentPrefix
            + "_"
            + index.ToString("D5", CultureInfo.InvariantCulture)
            + SegmentExtension;

        public bool IsSegmentName(string name)
        {
            string head = Path.GetFileName(SegmentPrefix) + "_";
            if (
                name.Length != head.Length + 5 + SegmentExtension.Length
                || !name.StartsWith(head, StringComparison.Ordinal)
                || !name.EndsWith(SegmentExtension, StringComparison.Ordinal)
            )
                return false;

            return name.AsSpan(head.Length, 5).IndexOfAnyExceptInRange('0', '9') < 0;
        }
    }

    public async Task<ResumeDecision> ResolveAsync(ResumeRequest request, CancellationToken ct)
    {
        List<HlsOutput> outputs = ParseOutputs(request.Commands);
        string? refusal = RefuseStatically(request, outputs);
        if (refusal is not null)
            return StartOver(request, refusal);

        string markerPath = marker.PathFor(
            request.OutputDirectory,
            EncodeResumeMarker.HashArguments(request.Commands)
        );

        ResumeDecision decision;
        try
        {
            decision = await DecideAsync(request, outputs, markerPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            decision = ResumeDecision.StartAtZero($"resume check failed: {ex.Message}");
        }

        if (decision.Resume)
        {
            logger.LogInformation(
                "[{CorrelationId}] Encode resumes at segment {Segment} from {Ms} ms",
                request.CorrelationId,
                decision.StartSegment,
                decision.ResumeMs
            );
            return decision;
        }

        await BeginFreshAsync(request, outputs, markerPath, ct);
        return StartOver(request, decision.Reason);
    }

    /// <summary>Starts the encode over after a failed resume rebuild, so its own restart can resume.</summary>
    public async Task AbandonAsync(ResumeRequest request, string reason, CancellationToken ct)
    {
        string markerPath = marker.PathFor(
            request.OutputDirectory,
            EncodeResumeMarker.HashArguments(request.Commands)
        );
        await BeginFreshAsync(request, ParseOutputs(request.Commands), markerPath, ct);
        StartOver(request, reason);
    }

    // Segments of an older run would join the new ones at the next restart, so they go first.
    private async Task BeginFreshAsync(
        ResumeRequest request,
        List<HlsOutput> outputs,
        string markerPath,
        CancellationToken ct
    )
    {
        try
        {
            await ClearStaleFilesAsync(request.OutputDirectory, outputs, ct);
            await marker.WriteAsync(markerPath, request.CorrelationId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "[{CorrelationId}] Resume marker not written; this encode cannot be resumed",
                request.CorrelationId
            );
        }
    }

    private async Task ClearStaleFilesAsync(
        string outputDirectory,
        List<HlsOutput> outputs,
        CancellationToken ct
    )
    {
        await marker.ClearAllAsync(outputDirectory, ct);
        foreach (HlsOutput output in outputs)
        {
            string firstSegment = SegmentPath(outputDirectory, output, 0);
            string folder = storage.GetParent(firstSegment) ?? outputDirectory;
            List<string> stale = [];
            await foreach (StorageEntry entry in storage.ListAsync(folder, "*", false, ct))
            {
                if (!entry.IsDirectory && output.IsSegmentName(storage.GetName(entry.Path)))
                    stale.Add(entry.Path);
            }

            foreach (string path in stale)
                await storage.DeleteAsync(path, ct);
        }
    }

    private async Task<ResumeDecision> DecideAsync(
        ResumeRequest request,
        List<HlsOutput> outputs,
        string markerPath,
        CancellationToken ct
    )
    {
        if (!await marker.ExistsAsync(markerPath, ct))
            return ResumeDecision.StartAtZero(
                "resume marker missing, or written for other arguments"
            );

        foreach (HlsOutput output in outputs)
        {
            if (await IsFinishedAsync(request.OutputDirectory, output, ct))
                return ResumeDecision.StartAtZero("a finished playlist is already there");
        }

        int completeSegments = outputs.Min(o => CountSegments(request.OutputDirectory, o)) - 1;
        if (completeSegments < 1)
            return ResumeDecision.StartAtZero("no complete segment to continue from");

        HlsOutput first = outputs[0];
        bool video = request.Plan.VideoOutputs.Length > 0;
        double? startPts = await prober.FirstPacketPtsAsync(
            SegmentPath(request.OutputDirectory, first, 0),
            video,
            ct
        );
        double? resumePts = await prober.FirstPacketPtsAsync(
            SegmentPath(request.OutputDirectory, first, completeSegments),
            video,
            ct
        );
        double? delay = await prober.FirstPacketDelayAsync(
            SegmentPath(request.OutputDirectory, first, 0),
            video,
            ct
        );
        if (startPts is null || resumePts is null || delay is null)
            return ResumeDecision.StartAtZero("segment probe failed");

        long resumeMs = (long)Math.Round((resumePts.Value - startPts.Value) * 1000);
        long delayMs = (long)Math.Round(delay.Value * 1000);
        return resumeMs <= 0
            ? ResumeDecision.StartAtZero("resume time is not after the start")
            : new(true, completeSegments, resumeMs, "resuming", markerPath, delayMs);
    }

    /// <summary>
    /// Makes every HLS output write from the resume segment, stamped at the resume time.
    /// </summary>
    public static OutputPlan ApplyTo(OutputPlan plan, ResumeDecision decision)
    {
        // A fresh run's first video packet sits its B-frame delay after the start; the offset adds it back.
        Dictionary<string, string> videoFlags = Flags(
            decision,
            (decision.ResumeMs + decision.TimestampDelayMs) / 1000.0,
            "F3"
        );

        return plan with
        {
            VideoOutputs =
            [
                .. plan.VideoOutputs.Select(v =>
                    v with
                    {
                        ExtraFlags = Merge(v.ExtraFlags, videoFlags),
                    }
                ),
            ],
            AudioOutputs =
            [
                .. plan.AudioOutputs.Select(a =>
                    a.Action == StreamAction.Transcode
                        ? a with
                        {
                            ExtraFlags = Merge(
                                a.ExtraFlags,
                                Flags(decision, AudioOffsetSeconds(a, decision), "F6")
                            ),
                        }
                        : a
                ),
            ],
        };
    }

    // The first AAC packet of a resumed run must land on the frame grid a full run uses.
    // A full run's muxer adds the encoder delay back; a resumed run's does not.
    private static double AudioOffsetSeconds(AudioOutputPlan audio, ResumeDecision decision)
    {
        double resumeSeconds = decision.ResumeMs / 1000.0;
        int delayFrames = audio.EncoderName switch
        {
            "libfdk_aac" => 2,
            "aac" => 1,
            _ => 0,
        };
        if (delayFrames == 0 || audio.SampleRate <= 0)
            return resumeSeconds;

        double frameSeconds = AacFrameSamples / (double)audio.SampleRate;
        return (Math.Ceiling(resumeSeconds / frameSeconds) + delayFrames) * frameSeconds;
    }

    private static Dictionary<string, string> Flags(
        ResumeDecision decision,
        double offsetSeconds,
        string format
    ) =>
        new()
        {
            ["-start_number"] = decision.StartSegment.ToString(CultureInfo.InvariantCulture),
            ["-output_ts_offset"] = offsetSeconds.ToString(format, CultureInfo.InvariantCulture),
        };

    private static Dictionary<string, string> Merge(
        Dictionary<string, string>? existing,
        Dictionary<string, string> flags
    )
    {
        Dictionary<string, string> merged = new(existing ?? []);
        foreach ((string key, string value) in flags)
            merged[key] = value;

        return merged;
    }

    private static string? RefuseStatically(ResumeRequest request, List<HlsOutput> outputs)
    {
        if (request.Plan.Format != OutputFormat.Hls)
            return "the plan is not an HLS encode";
        if (request.Pass != EncodingPass.Single)
            return "a pass of a two-pass encode has no segments to continue";
        if (request.DurationLimit is not null)
            return "a duration limit is set";
        if (request.ResumeFromMs is > 0)
            return "a crash-checkpoint seek is already set";
        if (request.Plan.SubtitleOutputs.Any(s => s.Policy == SubtitlePolicy.BurnIn))
            return "burn-in subtitles restart their filter clock at zero";
        if (BuildStage.IsCopyMode(request.Plan))
            return "a copy output cannot be cut at a segment boundary";
        if (request.Plan.Drm is not null && request.Plan.Drm.Method != DrmMethod.None)
            return "DRM keys are prepared per run";
        if (request.Plan.AudioOutputs.Any(UsesLoudnorm))
            return "loudnorm carries state from the start of the audio";
        if (outputs.Count == 0)
            return "the commands write no HLS segments in a known name pattern";

        return null;
    }

    private static bool UsesLoudnorm(AudioOutputPlan audio) =>
        audio.Action == StreamAction.Transcode
        && audio.AudioFilter?.Contains("loudnorm", StringComparison.Ordinal) == true;

    private ResumeDecision StartOver(ResumeRequest request, string reason)
    {
        logger.LogInformation(
            "[{CorrelationId}] Encode starts at 0: {Reason}",
            request.CorrelationId,
            reason
        );
        return ResumeDecision.StartAtZero(reason);
    }

    private async Task<bool> IsFinishedAsync(
        string outputDirectory,
        HlsOutput output,
        CancellationToken ct
    )
    {
        if (output.Playlist is null)
            return false;

        string playlistPath = storage.CombinePath(outputDirectory, output.Playlist);
        return await storage.ExistsAsync(playlistPath, ct)
            && (await storage.ReadAllTextAsync(playlistPath, ct)).Contains(
                EndListTag,
                StringComparison.Ordinal
            );
    }

    private int CountSegments(string outputDirectory, HlsOutput output)
    {
        int count = 0;
        while (storage.Exists(SegmentPath(outputDirectory, output, count)))
            count++;

        return count;
    }

    private string SegmentPath(string outputDirectory, HlsOutput output, int index) =>
        storage.CombinePath(outputDirectory, output.SegmentFile(index));

    /// <summary>
    /// The segment pattern and playlist of every HLS output, read from the
    /// arguments so the naming stays owned by the output strategy.
    /// </summary>
    internal static List<HlsOutput> ParseOutputs(FfmpegCommand[] commands)
    {
        List<HlsOutput> outputs = [];
        foreach (FfmpegCommand command in commands)
        {
            string[] args = command.Arguments;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "-hls_segment_filename")
                    continue;

                int token = args[i + 1].IndexOf(SegmentNumberToken, StringComparison.Ordinal);
                if (token < 0)
                    return [];

                string? playlist = args.Skip(i + 2)
                    .FirstOrDefault(a => a.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
                outputs.Add(
                    new(
                        args[i + 1][..token],
                        args[i + 1][(token + SegmentNumberToken.Length)..],
                        playlist
                    )
                );
            }
        }

        return outputs;
    }
}
