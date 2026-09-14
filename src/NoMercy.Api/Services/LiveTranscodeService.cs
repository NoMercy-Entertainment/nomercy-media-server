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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Api.Controllers.V1.Streaming.Dtos;
using NoMercy.Authorization.LiveIngest;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Devices;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Encoder.LiveTranscode.Protocol;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;
using NoMercyQueue.Core.Resources;
using EncoderMediaInfo = NoMercy.Encoder.Analysis.MediaInfo;

namespace NoMercy.Api.Services;

/// <inheritdoc />
public class LiveTranscodeService(
    ILiveEncoder liveEncoder,
    ILiveStreamingService streamingService,
    ILivePlaylistBuilder playlistBuilder,
    ISessionManager sessionManager,
    IMediaAnalyzer mediaAnalyzer,
    ILiveQualitySelector qualitySelector,
    IPlaybackDecisionEngine decisionEngine,
    SpeedIndex speedIndex,
    IResourceBudget budget,
    IDbContextFactory<MediaContext> contextFactory,
    LiveSessionLimits sessionLimits,
    IStorage storage,
    IDeviceCapabilityRegistry capabilityRegistry,
    IDeviceAwareVariantSelector variantSelector,
    ILiveIngestKeyStore ingestKeyStore,
    ILogger<LiveTranscodeService> logger,
    ILiveSessionTransport? transport = null
) : ILiveTranscodeService
{
    // How long a segment request blocks waiting for the encoder to produce the
    // requested (on-demand) segment before giving up, and how often it rechecks.
    private static readonly TimeSpan SegmentWaitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SegmentPollInterval = TimeSpan.FromMilliseconds(200);

    public IReadOnlyList<LiveSessionDto> ListSessions()
    {
        IReadOnlyList<LiveSessionSnapshot> snapshots = streamingService.GetActiveSessions();
        return
        [
            .. snapshots.Select(s => new LiveSessionDto(
                SessionId: s.SessionId,
                State: s.State.ToString(),
                QualityId: s.QualityId,
                QualityLabel: s.QualityLabel,
                Width: s.Width,
                Height: s.Height,
                BitrateKbps: s.BitrateKbps,
                PositionSeconds: s.PositionSeconds,
                BufferAheadSeconds: s.BufferAheadSeconds,
                SegmentCount: s.SegmentCount,
                IsComplete: s.IsComplete,
                LastAccess: s.LastAccess
            )),
        ];
    }

    public async Task<LiveResult> StartSessionAsync(
        Guid userId,
        StartLiveSessionRequest request,
        string? deviceId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.VideoFileId))
            return LiveResult.BadRequest("video_file_id is required");

        if (request.ClientCaps is null)
            return LiveResult.BadRequest("client_caps is required");

        if (!Ulid.TryParse(request.VideoFileId, out Ulid videoFileId))
            return LiveResult.BadRequest("video_file_id is not a valid identifier");

        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        AuthorizedFile? resolved = await ResolveAuthorizedFileAsync(
            context,
            videoFileId,
            userId,
            ct
        );
        if (resolved is null)
            return LiveResult.NotFound("Video file not found or you lack access");

        // Reconcile accounting against the live runtimes first: a crashed FFmpeg or
        // an abandoned tab can leave a session counted against the cap after its
        // runtime is gone — the "0 active but max reached" symptom.
        sessionManager.PruneDeadSessions(streamingService.ActiveSessionIds);

        if (!sessionManager.CanStartSession(userId.ToString()))
        {
            // Still at the cap with live sessions. Starting playback abandons the
            // previous stream without a clean stop (a reload or item switch), so
            // evict this user's stalest session to make room — the same way Plex and
            // Jellyfin supersede a replaced playback session rather than refusing.
            await EvictStalestUserSessionAsync(userId.ToString());

            if (!sessionManager.CanStartSession(userId.ToString()))
                return LiveResult.ServiceUnavailable("Maximum concurrent live sessions reached");
        }

        // The source codec is what drives the direct-play-vs-transcode decision, and
        // NoMercy's own HLS output is frequently HEVC 10-bit — the exact thing a
        // browser without HEVC needs transcoded — so the file MUST be probed, an
        // ".m3u8" name proves nothing about compatibility. A probe failure (backend
        // unreachable, transient NFS/S3 error, corrupt header) must NOT 500 the whole
        // playback: fall back to handing the client the file's own static URL. If it
        // is browser-playable it just plays; if not, the client surfaces a normal
        // playback error instead of a scary 500 on every start.
        EncoderMediaInfo mediaInfo;
        try
        {
            mediaInfo = await mediaAnalyzer.AnalyzeAsync(
                resolved.InputPath,
                resolved.ExtraInputArgs,
                ct
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Live analyze failed for {VideoFileId}; falling back to direct-play of the static source",
                videoFileId
            );
            return DirectPlayResult(
                videoFileId,
                resolved.File,
                "Source could not be analyzed; serving the original file"
            );
        }

        DeviceCapabilities? deviceCaps = null;
        if (deviceId is not null)
        {
            deviceCaps =
                capabilityRegistry.Get(deviceId)
                ?? await capabilityRegistry.LoadFromDbAsync(deviceId, ct);
        }

        ClientCapabilities clientCaps = variantSelector.ApplyDeviceCaps(
            ToClientCapabilities(request.ClientCaps),
            deviceCaps
        );

        if (deviceCaps is not null)
            logger.LogInformation(
                "Live session for device {DeviceId}: caps channels={Ch} ramTier={Tier}",
                deviceId,
                deviceCaps.MaxAudioChannels,
                deviceCaps.RamTier
            );

        PlaybackDecision playbackDecision;
        try
        {
            playbackDecision = decisionEngine.Decide(mediaInfo, clientCaps);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PlaybackDecisionEngine.Decide threw; falling back to transcode");
            playbackDecision = new(PlaybackAction.TranscodeVideo, "Decision engine error", null);
        }

        // Transcoding a file that could have played untouched costs a CPU, a
        // startup delay and picture quality, and the reason was computed and then
        // dropped — so the choice was invisible from outside.
        logger.LogInformation(
            "Playback decision for video file {VideoFileId}: {Action} ({Reason})",
            videoFileId,
            playbackDecision.Action,
            playbackDecision.Reason ?? "compatible with client capabilities"
        );

        if (playbackDecision.Action == PlaybackAction.DirectPlay)
            return DirectPlayResult(
                videoFileId,
                resolved.File,
                "File is compatible with client capabilities"
            );

        // The client picks the audio track from the episode's own language list and
        // passes the ISO code. Absent (older client), fall back to English so a
        // Japanese-default file still opens in English.
        List<string> preferredLanguages = request.AudioLanguage is { Length: > 0 } lang
            ? [lang]
            : ["eng"];

        // A NoMercy-encoded file already ships each audio track as a separate
        // browser-ready HLS rendition, so the session transcodes video only and its
        // master playlist points the player at those renditions — instant switching,
        // no audio re-encode.
        List<LiveAudioRendition> fileRenditions = BuildAudioRenditions(
            resolved.File,
            preferredLanguages[0]
        );
        bool useFileRenditions = fileRenditions.Count > 0;

        // A raw source (a lossless remux, disc rip — no pre-encoded renditions) with
        // its own audio: transcode video-only and spin up a per-language audio-only
        // child so every language lands in the menu, each transcoded to AAC. Only
        // when the source has no renditions AND has audio streams to expose.
        bool rawMultiAudio = !useFileRenditions && mediaInfo.AudioStreams.Count > 0;

        int audioStreamIndex = LiveAudioSelector.Select(mediaInfo.AudioStreams, preferredLanguages);

        LiveEncodeRequest liveRequest = new(
            InputPath: resolved.InputPath,
            CachedInfo: mediaInfo,
            Client: clientCaps,
            StartPosition: TimeSpan.FromSeconds(Math.Max(0, request.StartTimeSeconds)),
            PreferredQuality: request.PreferredQuality,
            ExtraInputArgs: resolved.ExtraInputArgs,
            AudioStreamIndex: audioStreamIndex,
            // Video-only whenever audio comes from a separate track set (existing
            // renditions or per-language children); muxed only for the last-resort
            // single-track fallback.
            VideoOnly: useFileRenditions || rawMultiAudio
        );

        ILiveSession session;
        try
        {
            session = await liveEncoder.StartAsync(liveRequest, ct);
        }
        catch (EncoderRuntimeException ex)
        {
            return LiveResult.Encoder(ex.HttpStatusCode, ex.Shape);
        }
        catch (InvalidOperationException ex)
        {
            return LiveResult.ServiceUnavailable(ex.Message);
        }

        sessionManager.RegisterSession(session, userId.ToString());

        // Tie the self-ingest key to the session so it dies on teardown, not just
        // on its absolute-lifetime backstop. Null for local/disc sources that read
        // straight off the filesystem and never touch the HTTP serving port.
        if (resolved.IngestKey is not null)
            ingestKeyStore.BindSession(resolved.IngestKey, session.SessionId);

        // Assemble the master's audio list: the file's own renditions when it has
        // them, otherwise a per-language live audio child for a raw source. Either
        // way the runtime carries the list and the client loads the master; a source
        // with no audio at all loads the plain (video-only) media playlist.
        List<LiveAudioRendition> masterRenditions =
            useFileRenditions ? fileRenditions
            : rawMultiAudio
                ? await StartAudioChildrenAsync(
                    session.SessionId,
                    liveRequest,
                    mediaInfo.AudioStreams,
                    audioStreamIndex,
                    ct
                )
            : [];
        bool useMaster = masterRenditions.Count > 0;

        if (useMaster)
            streamingService.StampAudioRenditions(session.SessionId, masterRenditions);

        string playlistUrl = useMaster
            ? $"/api/v1/streaming/live/sessions/{session.SessionId}/master.m3u8"
            : $"/api/v1/streaming/live/sessions/{session.SessionId}/playlist.m3u8";
        LiveQuality quality = session.CurrentQuality;
        DateTime expiresAt = DateTime.UtcNow.AddMinutes(sessionLimits.IdleTimeoutMinutes);
        SelectedVariantDto selectedVariant = new(
            Codec: quality.Codec.ToString(),
            Width: quality.Width,
            Height: quality.Height,
            BitrateKbps: quality.BitrateKbps
        );

        return LiveResult.Ok(
            new StartLiveSessionResponse(session.SessionId, playlistUrl, quality.Id, quality.Label)
            {
                SelectedVariant = selectedVariant,
                ExpiresAt = expiresAt,
            }
        );
    }

    public LiveResult GetMasterPlaylist(string sessionId)
    {
        if (!streamingService.TryGetRuntime(sessionId, out LiveRuntimeSession runtime))
            return SessionGoneOrNotFound(sessionId);

        runtime.TouchLastAccess();
        LiveQuality quality = runtime.Session.CurrentQuality;

        // The video variant URI is relative so the client resolves it against the
        // master's own URL; the media playlist and its segment URLs are unchanged.
        LiveMasterPlaylistRequest request = new(
            VideoPlaylistUri: "playlist.m3u8",
            Width: quality.Width,
            Height: quality.Height,
            BitrateKbps: quality.BitrateKbps,
            AudioRenditions: runtime.AudioRenditions
        );
        string master = playlistBuilder.BuildMaster(request);
        return LiveResult.Ok(master);
    }

    public LiveResult GetPlaylist(string sessionId)
    {
        if (!streamingService.TryGetRuntime(sessionId, out LiveRuntimeSession runtime))
            return SessionGoneOrNotFound(sessionId);

        runtime.TouchLastAccess();
        string segmentUrlTemplate =
            $"/api/v1/streaming/live/sessions/{sessionId}/segment/{runtime.CurrentEpoch}/{{index}}.ts";
        LivePlaylistRequest request = new(
            SessionId: sessionId,
            Segments: runtime.SnapshotSegments(),
            TargetSegmentDuration: runtime.TargetSegmentDuration,
            IsComplete: runtime.IsComplete,
            SegmentUrlTemplate: segmentUrlTemplate,
            TotalDuration: runtime.CachedMediaInfo?.Duration
        );
        string playlist = playlistBuilder.Build(request);
        return LiveResult.Ok(playlist);
    }

    public async Task<LiveResult> GetSegmentAsync(
        string sessionId,
        string epoch,
        int index,
        CancellationToken ct
    )
    {
        if (!streamingService.TryGetRuntime(sessionId, out LiveRuntimeSession runtime))
            return SessionGoneOrNotFound(sessionId);

        // No stale-epoch gate: the client holds one cached whole-runtime VOD
        // playlist whose segment URLs are minted once. A seek repositions the
        // encoder but must not invalidate those URLs — segments are absolutely
        // indexed, so the index alone identifies the content regardless of which
        // runner generation produced it. (The route still carries the epoch for
        // URL-shape compatibility; it is intentionally not matched here.)
        _ = epoch;

        // Pace transcoding from real client demand as a FALLBACK. A client that
        // reports its true position (LiveTranscodeHub.ReportPlayhead) owns the
        // playhead; this segment-request-derived estimate only applies while no
        // such authoritative report is current — see LiveSession.ReportPlaybackPosition.
        // Absent an authoritative report, the only signal of where the viewer is
        // comes from which segment it is fetching, and the player prefetches well
        // ahead of that — feeding it in unconditionally used to make BufferAhead
        // read as permanently low and let the encoder race to the end of the file.
        // Segments are absolutely indexed, so segment N sits at N×segDur.
        double segmentSeconds =
            runtime.TargetSegmentDuration.TotalSeconds > 0
                ? runtime.TargetSegmentDuration.TotalSeconds
                : 6;
        runtime.Session.ReportPlaybackPosition(
            TimeSpan.FromSeconds(index * segmentSeconds),
            authoritative: false
        );

        // The whole-runtime VOD playlist lists every segment up front, so hls.js
        // asks for the one at the playhead before the encoder has written it —
        // right after a seek (the client's beforeSeek repositions the encoder
        // concurrently), or when the transcode momentarily trails real time.
        // Block for the file to land rather than 404, which hls.js treats as a
        // fatal fragment-load error. Bounded so a genuinely dead encoder still
        // returns instead of hanging the request forever.
        // Deterministic on-disk path for this index, used as a fallback when the
        // in-memory buffer does not have the segment. After a seek the buffer is
        // cleared and the replacement runner writes segments to disk before the
        // drainer re-indexes them — serving from disk decouples delivery from that
        // bookkeeping so a produced segment is never withheld.
        string? diskPath = runtime.ScratchDirectory is { Length: > 0 } scratch
            ? storage.CombinePath(scratch, $"seg_{index:D5}.ts")
            : null;

        DateTime deadline = DateTime.UtcNow.Add(SegmentWaitTimeout);
        while (true)
        {
            if (
                runtime.TryGetSegment(index, out Segment segment)
                && storage.Exists(segment.FilePath)
            )
            {
                runtime.TouchLastAccess();
                Stream stream = storage.OpenRead(segment.FilePath);
                return LiveResult.Ok(stream);
            }

            if (diskPath is not null && storage.Exists(diskPath))
            {
                runtime.TouchLastAccess();
                Stream stream = storage.OpenRead(diskPath);
                return LiveResult.Ok(stream);
            }

            // The client is asking for a segment the encoder has not produced. If
            // the buffer-adaptive sweep suspended it (buffer looked full), wake it
            // now instead of waiting up to a full sweep interval — Resume is a
            // no-op unless the session is actually suspended, so this is safe to
            // call on every poll.
            if (runtime.Session.State == LiveSessionState.Buffered)
                runtime.Session.Resume();

            if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline)
                return LiveResult.NotFound($"Segment {index} is not ready yet");

            try
            {
                await Task.Delay(SegmentPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return LiveResult.NotFound($"Segment {index} is not ready yet");
            }

            runtime.TouchLastAccess();
        }
    }

    public LiveResult ReportPosition(string sessionId, ReportPositionRequest request)
    {
        if (!streamingService.TryGetRuntime(sessionId, out LiveRuntimeSession runtime))
            return SessionGoneOrNotFound(sessionId);

        double clampedSeconds = Math.Max(0, request.TimeSeconds);
        runtime.Session.ReportPlaybackPosition(
            TimeSpan.FromSeconds(clampedSeconds),
            authoritative: true
        );
        bool isPaused = runtime.Session.State == LiveSessionState.Buffered;
        return LiveResult.Ok(new ReportPositionResponse(clampedSeconds, isPaused));
    }

    public LiveResult ReportBufferHealth(string sessionId, ReportBufferHealthRequest request)
    {
        if (!streamingService.TryGetRuntime(sessionId, out LiveRuntimeSession runtime))
            return SessionGoneOrNotFound(sessionId);

        double clampedBufferedSeconds = Math.Max(0, request.BufferedSeconds);
        double clampedBandwidthKbps = Math.Max(0, request.ObservedBandwidthKbps);

        runtime.Session.ReportClientBufferHealth(
            TimeSpan.FromSeconds(clampedBufferedSeconds),
            (int)clampedBandwidthKbps
        );
        runtime.TouchLastAccess();

        return LiveResult.Ok(
            new ReportBufferHealthResponse(clampedBufferedSeconds, clampedBandwidthKbps)
        );
    }

    public async Task<LiveResult> ChangeQualityAsync(
        string sessionId,
        ChangeQualityRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.QualityId))
            return LiveResult.BadRequest("quality_id is required");

        if (!streamingService.TryGetRuntime(sessionId, out LiveRuntimeSession runtime))
            return SessionGoneOrNotFound(sessionId);

        if (runtime.CachedMediaInfo is null || runtime.ClientCapabilities is null)
            return LiveResult.ServiceUnavailable(
                "Session context not available for quality change"
            );

        LiveQuality[] available = qualitySelector.GetAvailableQualities(
            runtime.CachedMediaInfo,
            runtime.ClientCapabilities,
            speedIndex,
            budget
        );
        LiveQuality? newQuality = available.FirstOrDefault(q => q.Id == request.QualityId);
        if (newQuality is null)
            return LiveResult.NotFound(
                $"Quality '{request.QualityId}' is not available for this session"
            );

        await runtime.Session.ChangeQualityAsync(request.QualityId, newQuality, ct);
        runtime.TouchLastAccess();

        await PushIfTransportAsync(
                sessionId,
                new QualityChangedMessage(
                    NewQuality: newQuality,
                    Reason: QualityChangeReason.UserRequested,
                    SeekEpoch: runtime.CurrentEpoch
                ),
                ct
            )
            .ConfigureAwait(false);

        return LiveResult.Ok(new ChangeQualityResponse(newQuality.Id, newQuality.Label));
    }

    public async Task<LiveResult> SeekAsync(
        string sessionId,
        SeekRequest request,
        CancellationToken ct
    )
    {
        if (!streamingService.TryGetRuntime(sessionId, out LiveRuntimeSession runtime))
            return SessionGoneOrNotFound(sessionId);

        double clampedSeconds = Math.Max(0, request.PositionSeconds);
        int targetIndex = SegmentIndexAt(runtime, clampedSeconds);

        // Fast seek. The target segment has already been transcoded, so the client
        // can pull it over HTTP immediately. Re-spawning ffmpeg at the target would
        // kill the runner's forward lead and re-encode ground already covered: the
        // slow path, and the reason a "rewatch" seek used to stall. Skip it — just
        // move the reported playhead so the buffer-adaptive sweep paces from the new
        // position (it suspends the encoder if the seek left it far ahead, and a
        // later request past the transcoded frontier resumes it there). The check
        // reads the on-disk scratch too, not just the current runner's in-memory
        // buffer, because segment files persist across runner generations while the
        // buffer is cleared on every (re)spawn — without the disk check, every seek
        // after the first would re-encode content already sitting on disk. A target
        // that is genuinely absent — a forward jump past everything transcoded, or a
        // gap left by an earlier jump — falls through to a real re-spawn below.
        if (SegmentAlreadyTranscoded(runtime, targetIndex))
        {
            runtime.Session.ReportPlaybackPosition(
                TimeSpan.FromSeconds(clampedSeconds),
                authoritative: true
            );
            runtime.TouchLastAccess();

            await PushIfTransportAsync(
                    sessionId,
                    new SeekCompletedMessage(
                        NewPositionSeconds: clampedSeconds,
                        FirstSegmentIndex: targetIndex,
                        SeekEpoch: runtime.CurrentEpoch
                    ),
                    ct
                )
                .ConfigureAwait(false);

            return LiveResult.Ok(new SeekResponse(clampedSeconds));
        }

        await runtime.Session.SeekAsync(TimeSpan.FromSeconds(clampedSeconds), ct);
        runtime.TouchLastAccess();

        // targetIndex, not HighestSegmentIndex + 1: the runtime buffer is no
        // longer wiped on seek (see LiveSession.SeekAsync), so HighestSegmentIndex
        // can be an unrelated older maximum from before the seek. targetIndex is
        // the index the coverage-aware respawn (LiveEncoder.SpawnRunner via
        // LiveGapPlanner) actually targets and the client should fetch next.
        int firstSegmentIndex = targetIndex;
        await PushIfTransportAsync(
                sessionId,
                new SeekCompletedMessage(
                    NewPositionSeconds: clampedSeconds,
                    FirstSegmentIndex: firstSegmentIndex,
                    SeekEpoch: runtime.CurrentEpoch
                ),
                ct
            )
            .ConfigureAwait(false);

        return LiveResult.Ok(new SeekResponse(clampedSeconds));
    }

    // Absolute HLS segment index a wall-clock position maps to. Mirrors the
    // indexing GetSegmentAsync and the ffmpeg arg builder use so a seek's
    // "is it already transcoded" check lines up with what the client requests.
    private static int SegmentIndexAt(LiveRuntimeSession runtime, double positionSeconds)
    {
        double segmentSeconds =
            runtime.TargetSegmentDuration.TotalSeconds > 0
                ? runtime.TargetSegmentDuration.TotalSeconds
                : 6;
        return (int)(positionSeconds / segmentSeconds);
    }

    // Whether the segment at <paramref name="index"/> has already been produced
    // and is servable right now — in the current runner's in-memory buffer, or on
    // disk from any runner generation this session has spawned. Mirrors the two
    // sources GetSegmentAsync serves from, so a fast seek only skips the re-spawn
    // when the client is guaranteed to get the segment without one.
    private bool SegmentAlreadyTranscoded(LiveRuntimeSession runtime, int index)
    {
        if (runtime.TryGetSegment(index, out Segment buffered) && storage.Exists(buffered.FilePath))
            return true;

        return runtime.ScratchDirectory is { Length: > 0 } scratch
            && storage.Exists(storage.CombinePath(scratch, $"seg_{index:D5}.ts"));
    }

    public async Task EndSessionAsync(string sessionId, CancellationToken ct)
    {
        await PushIfTransportAsync(
                sessionId,
                new SessionEndedMessage(Reason: SessionEndReason.ClientDisconnected),
                ct
            )
            .ConfigureAwait(false);

        // LiveStreamingService.RemoveAsync now owns the complete teardown
        // (runtime disposal + session-manager removal + tombstone) atomically,
        // so callers no longer need a separate RemoveSession call.
        await streamingService.RemoveAsync(sessionId);

        // Kill the file's self-ingest key the moment the session ends so no
        // spent key survives on its absolute-lifetime backstop.
        ingestKeyStore.RevokeSession(sessionId);
    }

    private LiveResult SessionGoneOrNotFound(string sessionId) =>
        streamingService.WasRecentlyRemoved(sessionId)
            ? LiveResult.Gone("Live session has ended or expired")
            : LiveResult.NotFound("Live session not found");

    private async Task PushIfTransportAsync(string sessionId, object message, CancellationToken ct)
    {
        if (transport is null)
            return;

        try
        {
            await transport.SendToClientAsync(sessionId, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Transport push failed for {Event} on session {SessionId}",
                message.GetType().Name,
                sessionId
            );
        }
    }

    internal static ClientCapabilities ToClientCapabilities(ClientCapabilitiesDto dto)
    {
        return new(
            Video: dto.Video?.Select(ToVideoCodecCapability).ToArray() ?? [],
            Audio: dto.Audio?.Select(ToAudioCodecCapability).ToArray() ?? [],
            // dto.Containers is the legacy field name a client on the old flat
            // wire shape actually sends ("containers", not "supported_containers");
            // reading only the primary field silently dropped every legacy
            // client's container list, made IsContainerCompatible always false,
            // and forced a Remux session even when video/audio/HDR/10-bit all
            // matched.
            SupportedContainers: dto.SupportedContainers ?? dto.Containers ?? [],
            SupportsHdr: dto.SupportsHdr,
            MaxBitrateKbps: dto.MaxBitrateKbps,
            SupportedVideoCodecs: dto.VideoCodecs,
            SupportedAudioCodecs: dto.AudioCodecs,
            MaxWidth: dto.MaxWidth,
            MaxHeight: dto.MaxHeight,
            Supports10Bit: dto.Supports10Bit,
            MaxAudioChannels: dto.MaxAudioChannels > 0 ? dto.MaxAudioChannels : 2
        );
    }

    private static VideoCodecCapability ToVideoCodecCapability(VideoCodecCapabilityDto dto)
    {
        return new(
            Codec: dto.Codec,
            Profiles: dto.Profiles,
            MaxBitDepth: dto.MaxBitDepth,
            MaxWidth: dto.MaxWidth,
            MaxHeight: dto.MaxHeight,
            MaxFramerate: dto.MaxFramerate,
            HdrFormats: dto.HdrFormats,
            MaxBitrateKbps: dto.MaxBitrateKbps
        );
    }

    private static AudioCodecCapability ToAudioCodecCapability(AudioCodecCapabilityDto dto)
    {
        return new(
            Codec: dto.Codec,
            MaxChannels: dto.MaxChannels,
            Passthrough: dto.Passthrough,
            Decode: dto.Decode
        );
    }

    // The route DynamicStaticFilesMiddleware serves from, built exactly as
    // VideoPlaylistResponseDto does: /{folder-ULID}{sub-path}{/filename}. HostFolder
    // is the physical disk path (e.g. M:/…) and is NOT a valid route — the middleware
    // parses the first segment as a folder ULID, so a raw disk path 404s.
    private static string BuildServedUrl(VideoFile file) =>
        $"/{file.Share}{file.Folder}{file.Filename}";

    // The file's own pre-encoded HLS audio renditions, mapped to client-facing
    // URLs the session's master playlist references. Each rendition's FileName is
    // a folder-relative HLS path (e.g. "/audio_eng_aac/audio_eng_aac.m3u8"); the
    // served URL joins it to the file's base folder exactly as the static playlist
    // response does. The viewer's preferred language opens by default; the rest
    // stay available in the player's audio menu. Empty when the source has no
    // renditions (raw/disc), which routes the session to the muxed media playlist.
    private static List<LiveAudioRendition> BuildAudioRenditions(VideoFile file, string preferred)
    {
        List<IAudio> tracks =
            file.Metadata?.Audio?.Where(a =>
                    !string.IsNullOrWhiteSpace(a.FileName)
                    && a.FileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                )
                .ToList()
            ?? [];

        if (tracks.Count == 0)
            return [];

        int defaultIndex = tracks.FindIndex(a =>
            LiveAudioSelector.LanguageMatches(a.Language, preferred)
        );
        if (defaultIndex < 0)
            defaultIndex = 0;

        string baseFolder = $"/{file.Share}{file.Folder}";
        return
        [
            .. tracks.Select(
                (a, index) =>
                    new LiveAudioRendition(
                        Language: a.Language,
                        Uri: $"{baseFolder}{a.FileName}".EncodePath(),
                        IsDefault: index == defaultIndex
                    )
            ),
        ];
    }

    // Spawn one audio-only transcode per source language for a raw source, and
    // return the master's audio list pointing at each child's media playlist. The
    // viewer's language (defaultAudioIndex, already resolved) opens by default;
    // every language stays switchable because each child runs its own AAC transcode
    // sharing the video's segment boundaries. A child that fails to start is
    // skipped rather than sinking the whole session. The children are registered
    // for cascade disposal so they never outlive the video.
    private async Task<List<LiveAudioRendition>> StartAudioChildrenAsync(
        string parentSessionId,
        LiveEncodeRequest baseRequest,
        IReadOnlyList<AudioStreamInfo> audioStreams,
        int defaultAudioIndex,
        CancellationToken ct
    )
    {
        List<LiveAudioRendition> renditions = [];
        List<string> childSessionIds = [];

        for (int index = 0; index < audioStreams.Count; index++)
        {
            LiveEncodeRequest childRequest = baseRequest with { AudioStreamIndex = index };

            ILiveSession child;
            try
            {
                child = await liveEncoder.StartAudioRenditionAsync(childRequest, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to start live audio child for stream 0:a:{Index} of session {SessionId}",
                    index,
                    parentSessionId
                );
                continue;
            }

            childSessionIds.Add(child.SessionId);
            renditions.Add(
                new LiveAudioRendition(
                    Language: audioStreams[index].Language ?? "und",
                    Uri: $"/api/v1/streaming/live/sessions/{child.SessionId}/playlist.m3u8",
                    IsDefault: index == defaultAudioIndex
                )
            );
        }

        streamingService.StampChildAudioSessions(parentSessionId, childSessionIds);
        return renditions;
    }

    // Evict the caller's least-recently-accessed session. LastAccess is bumped on
    // every playlist/segment fetch, so an abandoned stream (reload/switch) is the
    // stalest and gets reclaimed first, leaving a genuinely active second-device
    // session alone. Any owned session with no live runtime is dropped outright.
    private async Task EvictStalestUserSessionAsync(string userId)
    {
        string? stalestId = null;
        DateTime stalestAccess = DateTime.MaxValue;

        foreach (string sessionId in sessionManager.GetUserSessionIds(userId))
        {
            if (!streamingService.TryGetRuntime(sessionId, out LiveRuntimeSession runtime))
            {
                sessionManager.RemoveSession(sessionId);
                continue;
            }

            if (runtime.LastAccess < stalestAccess)
            {
                stalestAccess = runtime.LastAccess;
                stalestId = sessionId;
            }
        }

        if (stalestId is not null)
            await streamingService.RemoveAsync(stalestId);
    }

    private LiveResult DirectPlayResult(Ulid videoFileId, VideoFile file, string reason)
    {
        string url = BuildServedUrl(file);
        logger.LogInformation("Direct-play for {VideoFileId}: {Url}", videoFileId, url);
        return LiveResult.Ok(
            new StartLiveSessionResponse(
                SessionId: string.Empty,
                PlaylistUrl: string.Empty,
                QualityId: string.Empty,
                QualityLabel: string.Empty
            )
            {
                Mode = "direct",
                DirectStreamUrl = url,
                DirectPlayReason = reason,
            }
        );
    }

    private async Task<AuthorizedFile?> ResolveAuthorizedFileAsync(
        MediaContext context,
        Ulid videoFileId,
        Guid userId,
        CancellationToken ct
    )
    {
        VideoFile? file = await context
            .VideoFiles.AsNoTracking()
            .Include(vf => vf.Metadata)
            .FirstOrDefaultAsync(vf => vf.Id == videoFileId, ct);
        if (file is null)
            return null;

        bool allowed = await UserHasAccessAsync(context, file, userId, ct);
        if (!allowed)
            return null;

        (string inputPath, string[]? extraInputArgs, string? ingestKey) = await ResolveInputAsync(
            context,
            file,
            ct
        );
        return new(file, inputPath, extraInputArgs, ingestKey);
    }

    // Resolve the source to something ffprobe/ffmpeg can actually open across every
    // storage backend. A library file's HostFolder is a driver-scope-relative key
    // (e.g. "Anime/Anime/Show/Ep") on a backend (NFS/SMB/S3/local) that ffmpeg often
    // cannot read directly — its own NFS client, for instance, fails where the
    // server's driver succeeds. So the transcoder self-ingests over the server's own
    // internal HTTP serving port: that path already streams every backend correctly
    // (it is what the browser plays), so ffmpeg only ever speaks plain HTTP. The
    // serving port is authenticated, so ffmpeg carries a single-use ingest key
    // scoped to this one file (not the viewer's bearer): it stays out of ffmpeg's
    // argv and outlives a short access token so long transcodes never 401 mid-run.
    // Non-library sources (disc rips, absolute paths — no folder ULID) keep their
    // direct filesystem path and no key.
    private async Task<(
        string InputPath,
        string[]? ExtraInputArgs,
        string? IngestKey
    )> ResolveInputAsync(MediaContext context, VideoFile file, CancellationToken ct)
    {
        string localPath = storage.CombinePath(file.HostFolder, file.Filename);

        if (!Ulid.TryParse(file.Share, out Ulid folderId))
            return (localPath, null, null);

        bool folderExists = await context
            .Folders.AsNoTracking()
            .AnyAsync(f => f.Id == folderId, ct);
        if (!folderExists)
            return (localPath, null, null);

        string servedPath = BuildServedUrl(file);
        string ingestKey = ingestKeyStore.Issue(servedPath);
        int httpPort = RuntimeServerSettings.Current.InternalServerPort + 1;
        string url = $"http://127.0.0.1:{httpPort}{servedPath.EncodePath()}";
        string[] headers = ["-headers", $"X-NoMercy-Ingest-Key: {ingestKey}\r\n"];
        return (url, headers, ingestKey);
    }

    private static async Task<bool> UserHasAccessAsync(
        MediaContext context,
        VideoFile file,
        Guid userId,
        CancellationToken ct
    )
    {
        if (file.MovieId is int movieId)
        {
            bool fromMovie = await context.Movies.AnyAsync(
                m => m.Id == movieId && m.Library.LibraryUsers.Any(u => u.UserId == userId),
                ct
            );
            if (fromMovie)
                return true;
        }

        if (file.EpisodeId is int episodeId)
        {
            bool fromEpisode = await context.Episodes.AnyAsync(
                e => e.Id == episodeId && e.Tv.Library.LibraryUsers.Any(u => u.UserId == userId),
                ct
            );
            if (fromEpisode)
                return true;
        }

        return false;
    }

    private sealed record AuthorizedFile(
        VideoFile File,
        string InputPath,
        string[]? ExtraInputArgs,
        string? IngestKey
    );
}
