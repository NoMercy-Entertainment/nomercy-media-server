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

using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;

namespace NoMercy.Encoder.LiveTranscode;

public class LiveEncoder(
    ILiveQualitySelector qualitySelector,
    ISessionManager sessionManager,
    ILiveStreamingService streamingService,
    ILiveFfmpegRunner runner,
    ILiveSegmentInventory segmentInventory,
    EncoderOptions options,
    SpeedIndex speedIndex,
    IResourceBudget budget,
    ILogger<LiveEncoder> logger
) : ILiveEncoder
{
    public Task<ILiveSession> StartAsync(LiveEncodeRequest request, CancellationToken ct)
    {
        logger.LogInformation("Starting live session for {Path}", request.InputPath);

        if (!sessionManager.CanStartSession())
        {
            throw new InvalidOperationException("Maximum concurrent live sessions reached");
        }

        LiveQuality quality = ResolveQuality(request);

        string sessionId = Ulid.NewUlid().ToString();
        LiveSession session = new(sessionId, quality);

        // The default audio track (resolved from the library's language
        // preference) so the first spawn — and every seek/quality re-spawn that
        // reads it back — maps the viewer's language, not just the file's first.
        session.SetAudioStreamIndex(request.AudioStreamIndex);

        // Seed the playhead at the start position. Segments are absolutely indexed
        // so a resumed/seeked session's transcoded position is absolute too; if the
        // playhead stayed at zero, BufferAhead would read as the whole start offset
        // and the buffer-adaptive sweep could suspend the encoder before the client
        // has fetched its first segment. Authoritative: the client's own heartbeat
        // reports (or, absent those, the segment-request-derived frontier) keep it
        // current thereafter.
        session.ReportPlaybackPosition(request.StartPosition, authoritative: true);
        session.SetState(LiveSessionState.Starting);

        // Flip to Transcoding eagerly so API consumers see the session as live
        // the moment StartAsync returns. The runner kicks in async — it will
        // downgrade to Error if FFmpeg fails to start.
        session.SetState(LiveSessionState.Transcoding);

        string outputDirectory = Path.Combine(
            options.ResolvedLiveTranscodeCachePath,
            $"lts-{sessionId}"
        );

        streamingService.Register(
            session,
            TimeSpan.FromSeconds(options.DefaultSegmentDurationSeconds),
            outputDirectory
        );

        streamingService.StampRequestContext(sessionId, request.CachedInfo, request.Client);

        // Build the run-input factory so SeekAsync / ChangeQualityAsync can
        // restart the runner without coupling LiveSession to LiveFfmpegRunner.
        // Quality is read from session.CurrentQuality at spawn time so that a
        // quality change performed before the factory fires uses the new value.
        async Task SpawnRunner(TimeSpan desiredPosition, CancellationToken runnerCt)
        {
            (LiveGapPlan? plan, int desiredIndex, int? lastIndex) = PlanGap(
                desiredPosition,
                outputDirectory,
                request.CachedInfo
            );

            if (plan is null)
            {
                // Every segment from here through the end of the file is already
                // on disk — spawning ffmpeg would re-encode content the client can
                // already fetch. Park the session so a later demand past the
                // covered region (a seek, a resume) resumes it.
                logger.LogDebug(
                    "Live session {SessionId}: desiredIndex {DesiredIndex} already covered through {LastIndex} — parking instead of re-encoding",
                    sessionId,
                    desiredIndex,
                    lastIndex
                );
                session.MarkRunnerIdle(runnerCt);
                return;
            }

            logger.LogDebug(
                "Live session {SessionId}: spawning gap-bounded run — desiredIndex={DesiredIndex} start={Start} stopAt={StopAt}",
                sessionId,
                desiredIndex,
                plan.Start,
                plan.StopAt
            );

            LiveRunInput runInput = new(
                InputPath: request.InputPath,
                OutputDirectory: outputDirectory,
                StartPosition: plan.Start,
                Quality: session.CurrentQuality,
                SegmentDurationSeconds: options.DefaultSegmentDurationSeconds,
                Client: request.Client,
                SourceInfo: request.CachedInfo,
                CustomArguments: request.CustomArguments,
                ExtraInputArgs: request.ExtraInputArgs,
                // Read back at spawn time so an audio switch performed before the
                // factory fires (like a quality change) uses the new track.
                AudioStreamIndex: session.CurrentAudioStreamIndex,
                VideoOnly: request.VideoOnly,
                StopPosition: plan.StopAt
            );

            try
            {
                await runner.RunAsync(runInput, session, runnerCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on seek/dispose
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Live runner task faulted for session {SessionId}", sessionId);
                // Without this the session lingers in Running state forever and the reaper
                // can't tell a wedged runner apart from a live one.
                session.SetState(LiveSessionState.Error);
            }
        }

        session.AttachRunnerFactory(SpawnRunner);

        session.MarkTranscodeStart();
        session.TrackRunnerTask(
            Task.Run(
                () => SpawnRunner(request.StartPosition, session.RunnerCancellation),
                CancellationToken.None
            )
        );

        logger.LogInformation(
            "Live session {SessionId} started at {Quality}",
            sessionId,
            quality.Label
        );

        return Task.FromResult<ILiveSession>(session);
    }

    public Task<ILiveSession> StartAudioRenditionAsync(
        LiveEncodeRequest request,
        CancellationToken ct
    )
    {
        // No cap check: an audio child rides on a video session that already
        // passed the cap. It carries a placeholder quality — audio-only runs skip
        // the whole video block, so width/height/encoder are never read.
        LiveQuality audioQuality = new(
            Id: "audio",
            Label: "Audio",
            Width: 0,
            Height: 0,
            Codec: Codecs.VideoCodecType.H264,
            BitrateKbps: 128,
            Encoder: "aac",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 1.0,
            CanRealtime: true
        );

        string sessionId = Ulid.NewUlid().ToString();
        LiveSession session = new(sessionId, audioQuality);
        session.SetAudioStreamIndex(request.AudioStreamIndex);
        session.ReportPlaybackPosition(request.StartPosition, authoritative: true);
        session.SetState(LiveSessionState.Transcoding);

        string outputDirectory = Path.Combine(
            options.ResolvedLiveTranscodeCachePath,
            $"lts-{sessionId}"
        );

        streamingService.Register(
            session,
            TimeSpan.FromSeconds(options.DefaultSegmentDurationSeconds),
            outputDirectory,
            isAudioRenditionChild: true
        );
        streamingService.StampRequestContext(sessionId, request.CachedInfo, request.Client);

        async Task SpawnRunner(TimeSpan desiredPosition, CancellationToken runnerCt)
        {
            (LiveGapPlan? plan, int desiredIndex, int? lastIndex) = PlanGap(
                desiredPosition,
                outputDirectory,
                request.CachedInfo
            );

            if (plan is null)
            {
                logger.LogDebug(
                    "Live audio rendition {SessionId}: desiredIndex {DesiredIndex} already covered through {LastIndex} — parking instead of re-encoding",
                    sessionId,
                    desiredIndex,
                    lastIndex
                );
                session.MarkRunnerIdle(runnerCt);
                return;
            }

            logger.LogDebug(
                "Live audio rendition {SessionId}: spawning gap-bounded run — desiredIndex={DesiredIndex} start={Start} stopAt={StopAt}",
                sessionId,
                desiredIndex,
                plan.Start,
                plan.StopAt
            );

            LiveRunInput runInput = new(
                InputPath: request.InputPath,
                OutputDirectory: outputDirectory,
                StartPosition: plan.Start,
                Quality: session.CurrentQuality,
                SegmentDurationSeconds: options.DefaultSegmentDurationSeconds,
                Client: request.Client,
                SourceInfo: request.CachedInfo,
                CustomArguments: request.CustomArguments,
                ExtraInputArgs: request.ExtraInputArgs,
                AudioStreamIndex: session.CurrentAudioStreamIndex,
                AudioRenditionOnly: true,
                StopPosition: plan.StopAt
            );

            try
            {
                await runner.RunAsync(runInput, session, runnerCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on seek/dispose
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Live audio runner faulted for session {SessionId}", sessionId);
                session.SetState(LiveSessionState.Error);
            }
        }

        session.AttachRunnerFactory(SpawnRunner);
        session.MarkTranscodeStart();
        session.TrackRunnerTask(
            Task.Run(
                () => SpawnRunner(request.StartPosition, session.RunnerCancellation),
                CancellationToken.None
            )
        );

        logger.LogInformation(
            "Live audio rendition {SessionId} started for stream 0:a:{Index}",
            sessionId,
            request.AudioStreamIndex
        );

        return Task.FromResult<ILiveSession>(session);
    }

    // Shared by both StartAsync's and StartAudioRenditionAsync's SpawnRunner: the
    // on-disk segment inventory is read fresh at every (re)spawn (never cached)
    // because a previous runner generation may have kept writing after this one
    // was superseded — a stale snapshot would re-open a gap that already closed.
    private (LiveGapPlan? Plan, int DesiredIndex, int? LastIndex) PlanGap(
        TimeSpan desiredPosition,
        string outputDirectory,
        MediaInfo sourceInfo
    )
    {
        int segmentDuration = options.DefaultSegmentDurationSeconds;
        int? lastIndex =
            sourceInfo.Duration > TimeSpan.Zero
                ? (int)Math.Ceiling(sourceInfo.Duration.TotalSeconds / segmentDuration) - 1
                : null;
        int desiredIndex =
            segmentDuration > 0 ? (int)(desiredPosition.TotalSeconds / segmentDuration) : 0;

        LiveGapPlan? plan = LiveGapPlanner.Plan(
            segmentInventory.Snapshot(outputDirectory),
            desiredIndex,
            segmentDuration,
            lastIndex
        );

        return (plan, desiredIndex, lastIndex);
    }

    private LiveQuality ResolveQuality(LiveEncodeRequest request)
    {
        if (request.PreferredQuality is not null)
        {
            LiveQuality[] available = qualitySelector.GetAvailableQualities(
                request.CachedInfo,
                request.Client,
                speedIndex,
                budget
            );

            LiveQuality? match = available.FirstOrDefault(q => q.Id == request.PreferredQuality);
            if (match is not null)
                return match;
        }

        return qualitySelector.SelectOptimal(
            request.CachedInfo,
            request.Client,
            speedIndex,
            budget
        );
    }
}
