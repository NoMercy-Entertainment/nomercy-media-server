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
using NoMercy.DiscFormat.Disc.Bdmv;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage;

namespace NoMercy.OpticalMedia.Live;

/// <summary>
/// Routes a disc title through the standard live HLS pipeline. Builds the
/// right ffmpeg input URL for the disc type:
///   Bluray  → <c>bluray:&lt;mount&gt;/</c> with <c>-playlist N</c>
///   DVD     → <c>&lt;mount&gt;/</c> with <c>-f dvdvideo -title N</c>
///   CD      → <c>&lt;mount&gt;</c> with <c>-f libcdio</c>
/// then probes via <see cref="IMediaAnalyzer"/> to populate the cached
/// <see cref="MediaInfo"/> the encoder needs, and hands off to
/// <see cref="ILiveEncoder.StartAsync"/>.
/// </summary>
public sealed class LiveDiscSession(
    IMediaAnalyzer mediaAnalyzer,
    ILiveEncoder liveEncoder,
    ILiveStreamingService streamingService,
    IStorageDriver storageDriver,
    ILogger<LiveDiscSession> logger
) : ILiveDiscSession
{
    public async Task<ILiveSession> StartAsync(
        DiscDrive drive,
        int titleIndex,
        TimeSpan startPosition,
        string? preferredQuality,
        AudioTrackSelection[] audioTracks,
        CancellationToken ct
    )
    {
        string inputPath = BuildInputPath(drive, titleIndex);
        string[] extraArgs = BuildExtraInputArgs(drive, titleIndex);
        logger.LogInformation(
            "Live disc session for {Drive} title {Title} → {InputPath} {ExtraArgs}",
            drive.Path,
            titleIndex,
            inputPath,
            string.Join(' ', extraArgs)
        );

        // Probe the disc input the same way the encoder probes any other
        // source. The analyzer wraps ffprobe + parses streams + chapters
        // into a MediaInfo the live runner consumes verbatim. Disc protocol
        // URLs (bluray:/dvd:) are not filesystem paths, so this must take the
        // extraInputArgs overload — the plain overload resolves inputPath
        // through IStorage.AcquireLocalPath, which is meaningless here, and
        // has no way to carry -playlist N ahead of -i.
        MediaInfo info = await mediaAnalyzer.AnalyzeAsync(inputPath, extraArgs, ct);

        // ffprobe's bluray: protocol never populates stream language tags —
        // the same gap BlurayDiscSource.ApplyMplsLanguages works around for
        // the probe/rip path by reading the playlist's own STN table
        // instead. Without this, every live audio rendition below is
        // stamped "und" and the player's track switcher shows indistinguishable
        // "Unknown" entries.
        if (drive.DiscType == OpticalDiscType.BluRay && info.AudioStreams.Count > 0)
        {
            IReadOnlyList<AudioStreamInfo> enriched = ApplyMplsAudioLanguages(
                info.AudioStreams,
                drive.Path,
                titleIndex
            );
            if (!ReferenceEquals(enriched, info.AudioStreams))
                info = info with { AudioStreams = enriched };
        }

        // ClientCapabilities defaults — the runner only needs format + codec
        // hints, the streaming service refines per-client when it stamps
        // the runtime context. Caller can override later via the existing
        // /streaming/live/sessions/* endpoints.
        ClientCapabilities client = new(
            SupportedVideoCodecs: [VideoCodecType.H264, VideoCodecType.H265],
            SupportedAudioCodecs: [AudioCodecType.Aac, AudioCodecType.Eac3],
            SupportedContainers: ["hls", "mp4"],
            MaxWidth: 1920,
            MaxHeight: 1080,
            SupportsHdr: false,
            Supports10Bit: false,
            MaxBitrateKbps: 8000,
            MaxAudioChannels: 2
        );

        // Included stream indices, de-duplicated and stable-ordered so the first
        // one the caller listed is the one the master marks DEFAULT=YES — mirrors
        // how the rip endpoint reads the same AudioTrackSelection[] shape.
        int[] selected = audioTracks
            .Where(a => a.Include)
            .Select(a => a.StreamIndex)
            .Distinct()
            .ToArray();

        LiveEncodeRequest request = new(
            InputPath: inputPath,
            CachedInfo: info,
            Client: client,
            StartPosition: startPosition,
            PreferredQuality: preferredQuality,
            ExtraInputArgs: extraArgs.Length > 0 ? extraArgs : null,
            AudioStreamIndex: selected.Length == 1 ? selected[0] : 0,
            // Two or more selected tracks: video carries no audio of its own —
            // each selected track gets its own AAC rendition child below, the
            // same split LiveTranscodeService uses for a raw multi-audio file.
            VideoOnly: selected.Length > 1
        );

        ILiveSession session = await liveEncoder.StartAsync(request, ct);

        if (selected.Length > 1)
        {
            List<LiveAudioRendition> renditions = await StartAudioRenditionsAsync(
                session.SessionId,
                request,
                info,
                selected,
                ct
            );

            if (renditions.Count > 0)
            {
                streamingService.StampAudioRenditions(session.SessionId, renditions);
            }
            else
            {
                // No audio child started: restart with the first track muxed in.
                await session.DisposeAsync();
                session = await liveEncoder.StartAsync(
                    request with { VideoOnly = false, AudioStreamIndex = selected[0] },
                    ct
                );
            }
        }

        return session;
    }

    // Spawns one audio-only child rendition per selected disc audio stream so
    // the live master playlist can expose every one the user picked, the same
    // way LiveTranscodeService.StartAudioChildrenAsync does for a raw
    // multi-audio file. A child that fails to start is skipped rather than
    // sinking the whole session — the viewer still gets video plus whichever
    // tracks did start.
    private async Task<List<LiveAudioRendition>> StartAudioRenditionsAsync(
        string parentSessionId,
        LiveEncodeRequest baseRequest,
        MediaInfo info,
        int[] selectedStreamIndices,
        CancellationToken ct
    )
    {
        List<LiveAudioRendition> renditions = [];
        List<string> childSessionIds = [];

        for (int position = 0; position < selectedStreamIndices.Length; position++)
        {
            int streamIndex = selectedStreamIndices[position];
            LiveEncodeRequest childRequest = baseRequest with { AudioStreamIndex = streamIndex };

            ILiveSession child;
            try
            {
                child = await liveEncoder.StartAudioRenditionAsync(childRequest, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to start live disc audio child for stream 0:a:{Index} of session {SessionId}",
                    streamIndex,
                    parentSessionId
                );
                continue;
            }

            childSessionIds.Add(child.SessionId);
            string? language = info.AudioStreams.ElementAtOrDefault(streamIndex)?.Language;
            renditions.Add(
                new LiveAudioRendition(
                    Language: string.IsNullOrWhiteSpace(language) ? "und" : language,
                    Uri: $"/api/v1/streaming/live/sessions/{child.SessionId}/playlist.m3u8",
                    IsDefault: renditions.Count == 0
                )
            );
        }

        streamingService.StampChildAudioSessions(parentSessionId, childSessionIds);
        return renditions;
    }

    /// <summary>
    /// Reads BDMV/PLAYLIST/&lt;titleIndex&gt;.mpls and stamps each audio
    /// stream's language from the playlist's own STN table, position-matched
    /// against ffprobe's stream order. Mirrors
    /// <see cref="Sources.Bluray.BlurayDiscSource"/>'s own merge (including
    /// the TrueHD-dual-stream pairing — ffprobe demuxes a TrueHD track's
    /// embedded AC3 "compatible core" as its own stream, one mpls slot for
    /// the pair) rather than sharing code with it: the two call sites use
    /// different <c>AudioStreamInfo</c> record types
    /// (<see cref="NoMercy.Encoder.Analysis.AudioStreamInfo"/> here vs.
    /// <see cref="Sources.Bluray.AudioStreamInfo"/> there), so there is
    /// nothing generic to factor out without an interface neither side
    /// otherwise needs.
    /// </summary>
    private IReadOnlyList<AudioStreamInfo> ApplyMplsAudioLanguages(
        IReadOnlyList<AudioStreamInfo> streams,
        string drivePath,
        int titleIndex
    )
    {
        try
        {
            string trimmed = drivePath.TrimEnd('\\', '/');
            string mplsPath = Path.Combine(trimmed, "BDMV", "PLAYLIST", $"{titleIndex:D5}.mpls");

            // No mpls to read — return the original reference unchanged
            // rather than an equal-but-new array, so a caller comparing by
            // reference can tell nothing was enriched.
            if (!storageDriver.FileExists(mplsPath))
                return streams;

            using Stream stream = storageDriver.OpenRead(mplsPath);
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            MplsPlaylist playlist = MplsParser.Parse(buffer.ToArray());

            return MergeAudioLanguages(streams, playlist.AudioStreams);
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                ex,
                "Could not read mpls languages for {Drive} title {Title}: {Message}",
                drivePath,
                titleIndex,
                ex.Message
            );
            return streams;
        }
    }

    /// <summary>
    /// Pure position-matching merge, split out from <see cref="ApplyMplsAudioLanguages"/>
    /// so the TrueHD-pairing logic is unit-testable against an in-memory
    /// <see cref="MplsStream"/> list without needing real MPLS bytes or a
    /// fake <see cref="IStorageDriver"/> file.
    /// </summary>
    internal static AudioStreamInfo[] MergeAudioLanguages(
        IReadOnlyList<AudioStreamInfo> streams,
        IReadOnlyList<MplsStream> mplsAudioStreams
    )
    {
        AudioStreamInfo[] merged = new AudioStreamInfo[streams.Count];
        int mplsIndex = 0;
        for (int i = 0; i < streams.Count; i++)
        {
            string? language =
                mplsIndex < mplsAudioStreams.Count ? mplsAudioStreams[mplsIndex].Language : null;
            merged[i] = streams[i] with { Language = language };

            bool isTrueHdCorePair =
                streams[i].Codec == "truehd"
                && i + 1 < streams.Count
                && streams[i + 1].Codec == "ac3";
            if (isTrueHdCorePair)
            {
                i++;
                merged[i] = streams[i] with { Language = language };
            }
            mplsIndex++;
        }

        return merged;
    }

    /// <summary>
    /// Builds the disc-type-specific input URL. The title/playlist index is
    /// never encoded into the URL itself — ffmpeg's bluray: protocol has no
    /// query-string selector, and DVD/CD have none either. Every disc type
    /// selects its title via CLI flags instead, from
    /// <see cref="BuildExtraInputArgs"/>, mirroring the exact pattern
    /// <c>DiscRipper.cs</c> already uses for the same disc types.
    /// </summary>
    private static string BuildInputPath(DiscDrive drive, int titleIndex)
    {
        string trimmed = drive.Path.TrimEnd('\\', '/');
        return drive.DiscType switch
        {
            OpticalDiscType.BluRay => $"bluray:{trimmed}/",
            OpticalDiscType.Dvd => $"{trimmed}/",
            OpticalDiscType.Cd => drive.Path,
            _ => trimmed,
        };
    }

    /// <summary>
    /// ffmpeg input flags that must precede "-i" to select the right title —
    /// same per-type mapping as <c>DiscRipper.BuildRipArgs</c>.
    /// </summary>
    private static string[] BuildExtraInputArgs(DiscDrive drive, int titleIndex)
    {
        string index = titleIndex.ToString(CultureInfo.InvariantCulture);
        return drive.DiscType switch
        {
            OpticalDiscType.BluRay => ["-playlist", index],
            OpticalDiscType.Dvd => ["-f", "dvdvideo", "-title", index],
            OpticalDiscType.Cd => ["-f", "libcdio"],
            _ => [],
        };
    }
}
