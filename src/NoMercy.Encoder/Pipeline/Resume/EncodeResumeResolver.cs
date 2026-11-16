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
/// Finds where an interrupted HLS encode continues. A default (vod) encode
/// writes its playlists only when it ends, so a stopped encode leaves segment
/// files and no playlist; the files are counted instead. Anything the resume
/// cannot prove safe starts the encode over from zero.
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

        // The new run replaces whatever marker was there, so its own restart can resume.
        await marker.WriteAsync(markerPath, request.CorrelationId, ct);
        return StartOver(request, decision.Reason);
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
        if (startPts is null || resumePts is null)
            return ResumeDecision.StartAtZero("segment probe failed");

        long resumeMs = (long)Math.Round((resumePts.Value - startPts.Value) * 1000);
        return resumeMs <= 0
            ? ResumeDecision.StartAtZero("resume time is not after the start")
            : new(true, completeSegments, resumeMs, "resuming", markerPath);
    }

    /// <summary>
    /// Makes every HLS output write from the resume segment and stamp its
    /// timestamps at the resume time, so the new segments equal the ones an
    /// uninterrupted run would have written.
    /// </summary>
    public static OutputPlan ApplyTo(OutputPlan plan, ResumeDecision decision)
    {
        Dictionary<string, string> flags = new()
        {
            ["-start_number"] = decision.StartSegment.ToString(CultureInfo.InvariantCulture),
            ["-output_ts_offset"] = (decision.ResumeMs / 1000.0).ToString(
                "F3",
                CultureInfo.InvariantCulture
            ),
        };

        return plan with
        {
            VideoOutputs =
            [
                .. plan.VideoOutputs.Select(v =>
                    v with
                    {
                        ExtraFlags = Merge(v.ExtraFlags, flags),
                    }
                ),
            ],
            AudioOutputs =
            [
                .. plan.AudioOutputs.Select(a =>
                    a.Action == StreamAction.Transcode
                        ? a with
                        {
                            ExtraFlags = Merge(a.ExtraFlags, flags),
                        }
                        : a
                ),
            ],
        };
    }

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
        if (outputs.Count == 0)
            return "the commands write no HLS segments in a known name pattern";

        return null;
    }

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
