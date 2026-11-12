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
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.LiveTranscode.Protocol;
using NoMercy.Resources;
using NoMercy.Storage;

namespace NoMercy.Encoder.LiveTranscode;

public class LiveFfmpegRunner(
    IProcessRunner processRunner,
    EncoderOptions options,
    ILogger<LiveFfmpegRunner> logger,
    IStorage storage,
    INvencSessionCap nvencSessionCap,
    IHardwareCapabilities hardware,
    ICodecResolver codecResolver,
    IResourceBudget resourceBudget,
    ILiveSessionTransport? transport = null
) : ILiveFfmpegRunner
{
    internal const string PlaylistFileName = "index.m3u8";
    internal const string SegmentPrefix = "seg_";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GpuLeaseWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CpuLeaseWait = TimeSpan.FromMilliseconds(500);

    internal TimeSpan LeaseWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task RunAsync(LiveRunInput input, LiveSession session, CancellationToken ct)
    {
        // Enforce NVENC concurrent session cap before spawning FFmpeg.
        // Hardware-accelerated live encodes use the first GPU device name for
        // the error message; software encodes skip the check.
        bool requiresGpu =
            input.Quality.IsHardwareAccelerated && LiveFfmpegArgumentBuilder.EncodesVideo(input);
        string gpuName = hardware.Gpus.Count > 0 ? hardware.Gpus[0].Name : "GPU";

        try
        {
            nvencSessionCap.EnforceForGpuEncode(gpuName, requiresGpu);
        }
        catch (EncoderRuntimeException) when (requiresGpu)
        {
            // Every GPU encode slot is occupied — fall back to a software
            // encoder for THIS session instead of failing it outright. A
            // saturated GPU is a capacity problem, not a reason to break an
            // in-progress watch session when the CPU can pick up the slack.
            input = await FallBackToSoftwareAsync(input, session, ct).ConfigureAwait(false);
            requiresGpu = false;
        }

        // Acquire a resource-budget lease for the duration of this live session
        // so the queue scheduler sees GPU/CPU slots as occupied.
        // An audio-only rendition (AAC) is cheap — one thread, no GPU — so a raw
        // source's several language children don't each reserve a full video slot
        // and starve the video encoder.
        // A GPU live session still decodes and filters on the CPU — the live
        // path has no GpuAccelPlan — so it reserves the same share a queued
        // GPU encode with a CPU filter graph does. Under-reserving here let a
        // queued software encode start alongside a live session and stall it.
        int cpuThreads = input.AudioRenditionOnly ? 1 : 2;
        ResourceRequirement requirement = requiresGpu
            ? new(gpuName, GpuSlots: 1, CpuThreads: EncodeThreadBudget.GpuEncodeWithCpuFilters)
            : new ResourceRequirement(null, GpuSlots: 0, CpuThreads: cpuThreads);

        // Declared outside the try so the outer finally can always see whether
        // a lease was actually granted. Acquisition now happens INSIDE the try
        // — it used to run before it, so a throw from CreateDirectory /
        // AcquireLocalPath / BuildArguments below skipped the finally entirely
        // and leaked the GPU/CPU budget forever.
        ResourceLease? lease = null;

        try
        {
            logger.LogInformation(
                "Live FFmpeg waiting for resource lease: session {SessionId} GpuSlots={GpuSlots} CpuThreads={CpuThreads}",
                session.SessionId,
                requirement.GpuSlots,
                requirement.CpuThreads
            );

            long waitStart = Environment.TickCount64;

            if (requiresGpu)
            {
                lease = await AcquireWithinAsync(requirement, GpuLeaseWait, ct)
                    .ConfigureAwait(false);

                if (lease is null && !ct.IsCancellationRequested)
                {
                    // A queued encode holds the GPU; a viewer must not wait for it.
                    input = await FallBackToSoftwareAsync(input, session, ct).ConfigureAwait(false);
                    requirement = new(
                        null,
                        GpuSlots: 0,
                        CpuThreads: EncodeThreadBudget.SoftwareEncode
                    );
                    lease = await AcquireWithinAsync(requirement, CpuLeaseWait, ct)
                        .ConfigureAwait(false);

                    if (lease is null && !ct.IsCancellationRequested)
                    {
                        logger.LogWarning(
                            "No GPU or CPU lease for live session {SessionId}",
                            session.SessionId
                        );
                        session.SetState(LiveSessionState.Error);
                        await PushTranscodeErrorAsync(
                                session.SessionId,
                                "No encoder capacity is free right now",
                                EncodingErrorKind.ResourceExhausted,
                                recoverable: true
                            )
                            .ConfigureAwait(false);
                        return;
                    }
                }

                ct.ThrowIfCancellationRequested();
            }
            else
            {
                lease = await AcquireWithinAsync(requirement, LeaseWaitTimeout, ct)
                    .ConfigureAwait(false);

                if (lease is null && !ct.IsCancellationRequested)
                {
                    logger.LogError(
                        "Live FFmpeg gave up waiting for a resource lease after {Seconds} s: session {SessionId} GpuSlots={GpuSlots} CpuThreads={CpuThreads}",
                        LeaseWaitTimeout.TotalSeconds,
                        session.SessionId,
                        requirement.GpuSlots,
                        requirement.CpuThreads
                    );
                    session.SetState(LiveSessionState.Error);
                    await PushTranscodeErrorAsync(
                            session.SessionId,
                            "Timed out waiting for encoder resources",
                            EncodingErrorKind.ResourceExhausted,
                            recoverable: true
                        )
                        .ConfigureAwait(false);
                    return;
                }
            }

            logger.LogInformation(
                "Live FFmpeg got resource lease after {ElapsedMs} ms: session {SessionId} GpuSlots={GpuSlots} CpuThreads={CpuThreads}",
                Environment.TickCount64 - waitStart,
                session.SessionId,
                requirement.GpuSlots,
                requirement.CpuThreads
            );

            storage.CreateDirectory(input.OutputDirectory);

            await using LocalPathLease outputLease = storage.AcquireLocalPath(
                input.OutputDirectory
            );

            string[] arguments = BuildArguments(input);

            logger.LogInformation(
                "Live FFmpeg starting for session {SessionId} → {Dir}",
                session.SessionId,
                input.OutputDirectory
            );

            ProgressParser progressParser = new();
            HashSet<int> pushedSegments = [];
            using CancellationTokenSource stopPolling =
                CancellationTokenSource.CreateLinkedTokenSource(ct);

            Task pollingTask = Task.Run(
                () => PollForSegmentsAsync(input, session, pushedSegments, stopPolling.Token),
                stopPolling.Token
            );

            void OnStdOut(string line)
            {
                FfmpegProgressSnapshot? snapshot = progressParser.FeedLine(line);
                if (snapshot is not null && snapshot.Speed > 0)
                {
                    session.SetSpeed(snapshot.Speed);
                }
            }

            try
            {
                ProcessResult result = await processRunner.RunAsync(
                    options.FfmpegPath,
                    arguments,
                    OnStdOut,
                    null,
                    outputLease.Path,
                    ct
                );

                if (!result.IsSuccess && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Live FFmpeg for session {SessionId} exited with code {Code}. stderr: {StdErr}",
                        session.SessionId,
                        result.ExitCode,
                        Truncate(result.StdErr, 1000)
                    );
                    session.SetState(LiveSessionState.Error);
                    await PushTranscodeErrorAsync(
                            session.SessionId,
                            $"FFmpeg exited with code {result.ExitCode}"
                        )
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation(
                    "Live FFmpeg cancelled for session {SessionId}",
                    session.SessionId
                );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Live FFmpeg threw for session {SessionId}", session.SessionId);
                session.SetState(LiveSessionState.Error);
                await PushTranscodeErrorAsync(session.SessionId, ex.Message).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await stopPolling.CancelAsync();
                    await pollingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Segment poller for session {SessionId} faulted",
                        session.SessionId
                    );
                }

                // Final sweep after the poller stops — the process may have written
                // segments between the last scheduled poll and its exit.
                try
                {
                    PushNewSegments(input, session, pushedSegments);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "Final segment drain raised for session {SessionId}",
                        session.SessionId
                    );
                }
            }
        }
        finally
        {
            if (lease is not null)
                resourceBudget.Release(lease);

            // A bounded run (LiveRunInput.StopPosition set) finished the gap it was
            // spawned to fill, not the file — it reached content an earlier runner
            // generation already produced. Completing the channel here would end
            // the whole session even though playback continues; park it idle
            // instead so a later seek/resume can spawn a fresh runner against the
            // same live channel. An unbounded run (StopPosition null) genuinely
            // ran to EOF, so CompleteIfCurrentRunner's superseded-generation guard
            // applies as before — see LiveSession.CompleteIfCurrentRunner.
            if (input.StopPosition is null)
                session.CompleteIfCurrentRunner(ct);
            else
                session.MarkRunnerIdle(ct);
        }
    }

    // Delegates to LiveFfmpegArgumentBuilder — kept as a static passthrough so
    // existing callers/tests referencing LiveFfmpegRunner.BuildArguments don't
    // need to change. The actual argv construction is a pure function of the
    // input and doesn't need any of this class's runtime dependencies, so it
    // lives in its own file (browser-safe pixel format, GOP/keyframe
    // alignment, rate control, remux/audio-only branching, CustomArguments
    // merge — see LiveFfmpegArgumentBuilder for the reasoning behind each).
    internal static string[] BuildArguments(LiveRunInput input) =>
        LiveFfmpegArgumentBuilder.Build(input);

    // Resolves the software equivalent of the requested quality's codec and
    // swaps it in for THIS run when the GPU's NVENC session cap is exhausted.
    // Updates the session's reported CurrentQuality (so API/UI consumers and
    // any later seek/resume respawn see the fallback) and best-effort pushes
    // a QualityChangedMessage so the client knows playback continues at a
    // CPU-encoded quality rather than silently degrading.
    private async Task<LiveRunInput> FallBackToSoftwareAsync(
        LiveRunInput input,
        LiveSession session,
        CancellationToken ct
    )
    {
        ResolvedCodec software = codecResolver.Resolve(
            input.Quality.Codec,
            hardware,
            EncoderPreference.ForceSoftware
        );

        LiveQuality fallbackQuality = input.Quality with
        {
            Encoder = software.FfmpegEncoderName,
            IsHardwareAccelerated = false,
        };

        session.SetQuality(fallbackQuality);

        logger.LogWarning(
            "GPU unavailable for session {SessionId} — falling back to {Encoder}",
            session.SessionId,
            fallbackQuality.Encoder
        );

        await PushQualityChangedAsync(
                session.SessionId,
                fallbackQuality,
                QualityChangeReason.GpuFallbackToCpu,
                ct
            )
            .ConfigureAwait(false);

        return input with
        {
            Quality = fallbackQuality,
        };
    }

    private async Task PushQualityChangedAsync(
        string sessionId,
        LiveQuality quality,
        QualityChangeReason reason,
        CancellationToken ct
    )
    {
        if (transport is null)
            return;

        QualityChangedMessage message = new(NewQuality: quality, Reason: reason);

        try
        {
            await transport.SendToClientAsync(sessionId, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Transport push failed for QualityChanged on session {SessionId}",
                sessionId
            );
        }
    }

    private async Task PollForSegmentsAsync(
        LiveRunInput input,
        LiveSession session,
        HashSet<int> seen,
        CancellationToken ct
    )
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                PushNewSegments(input, session, seen);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(
                    ex,
                    "Segment poll transient error for session {SessionId}",
                    session.SessionId
                );
            }

            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void PushNewSegments(LiveRunInput input, LiveSession session, HashSet<int> seen)
    {
        string playlistPath = Path.Combine(input.OutputDirectory, PlaylistFileName);
        if (!storage.Exists(playlistPath))
            return;

        IReadOnlyList<(int Index, TimeSpan Duration)> entries = ParsePlaylist(playlistPath);

        foreach ((int index, TimeSpan duration) in entries)
        {
            string segmentFile = Path.Combine(
                input.OutputDirectory,
                $"{SegmentPrefix}{index:D5}.ts"
            );

            if (seen.Contains(index))
                continue;

            if (!storage.Exists(segmentFile))
            {
                // The m3u8 can reference a segment before the file has finished
                // its atomic rename — wait for the next poll.
                continue;
            }

            long size = 0;
            try
            {
                size = storage.Size(segmentFile);
            }
            catch (Exception)
            {
                // Race with rename — size stays 0, pick up real value next time
            }

            // Segments are absolutely indexed (see LiveFfmpegArgumentBuilder), so
            // a segment's start is its index times the target duration — not a sum
            // accumulated from this runner's first segment, which would be wrong
            // for a runner spawned mid-file by a seek.
            TimeSpan startTime = TimeSpan.FromSeconds((double)index * input.SegmentDurationSeconds);
            Segment segment = new(index, startTime, duration, segmentFile, size);
            session.PushSegment(segment);
            seen.Add(index);
        }
    }

    internal IReadOnlyList<(int Index, TimeSpan Duration)> ParsePlaylist(string playlistPath)
    {
        List<(int Index, TimeSpan Duration)> entries = [];

        string[] lines;
        try
        {
            using Stream stream = storage.OpenRead(playlistPath);
            using StreamReader reader = new(stream, Encoding.UTF8);
            List<string> lineList = [];
            while (reader.ReadLine() is string rawLine)
                lineList.Add(rawLine);
            lines = [.. lineList];
        }
        catch (IOException)
        {
            // File is mid-write; caller retries
            return entries;
        }

        return ParsePlaylistLines(lines);
    }

    internal static IReadOnlyList<(int Index, TimeSpan Duration)> ParsePlaylistLines(string[] lines)
    {
        List<(int Index, TimeSpan Duration)> entries = [];
        TimeSpan? pendingDuration = null;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                string payload = line[8..];
                int commaIdx = payload.IndexOf(',');
                string durationToken = commaIdx >= 0 ? payload[..commaIdx] : payload;

                if (
                    double.TryParse(
                        durationToken,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double seconds
                    )
                )
                {
                    pendingDuration = TimeSpan.FromSeconds(seconds);
                }
            }
            else if (pendingDuration is not null && !line.StartsWith('#'))
            {
                int? index = ExtractIndex(line);
                if (index is int idx)
                    entries.Add((idx, pendingDuration.Value));

                pendingDuration = null;
            }
        }

        return entries;
    }

    private static int? ExtractIndex(string segmentLine)
    {
        int prefixIdx = segmentLine.IndexOf(SegmentPrefix, StringComparison.Ordinal);
        if (prefixIdx < 0)
            return null;

        int start = prefixIdx + SegmentPrefix.Length;
        int end = segmentLine.IndexOf('.', start);
        if (end < 0)
            return null;

        string digits = segmentLine[start..end];
        return int.TryParse(digits, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    private static string Truncate(string value, int max) =>
        value.Length > max ? value[..max] : value;

    private async Task<ResourceLease?> AcquireWithinAsync(
        ResourceRequirement requirement,
        TimeSpan wait,
        CancellationToken ct
    )
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);

        try
        {
            return await resourceBudget
                .AcquireAsync(requirement, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task PushTranscodeErrorAsync(
        string sessionId,
        string message,
        EncodingErrorKind kind = EncodingErrorKind.ProcessCrashed,
        bool recoverable = false
    )
    {
        if (transport is null)
            return;

        TranscodeErrorMessage errorMessage = new(
            Kind: kind,
            Message: message,
            Recoverable: recoverable
        );

        try
        {
            await transport
                .SendToClientAsync(sessionId, errorMessage, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Transport push failed for TranscodeError on session {SessionId}",
                sessionId
            );
        }
    }
}
