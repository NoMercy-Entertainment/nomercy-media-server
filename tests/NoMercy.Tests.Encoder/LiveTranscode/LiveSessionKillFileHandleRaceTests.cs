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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.LiveTranscode;

/// <summary>
/// Real-process regression for the "killing a live session on the TV trips
/// the server" bug: a real ffmpeg is spawned against a real video file, the
/// session is killed the way LiveStreamingService.RemoveAsync does in
/// production (cancel -> dispose -> delete scratch), and the test asserts
/// the scratch directory actually gets deleted — no IOException swallowed by
/// TryDeleteScratch's best-effort catch. Before the fix, LiveSession.DisposeAsync
/// only cancelled _runnerCts and returned; ffmpeg's file handles on the
/// scratch directory were often still open when RemoveAsync called
/// TryDeleteScratch immediately after, so this failed non-deterministically.
/// Requires a real ffmpeg on PATH or at %FFMPEG_PATH%; skips otherwise.
/// </summary>
[Trait("Category", "Integration")]
public class LiveSessionKillFileHandleRaceTests
{
    private static string? FindFfmpeg()
    {
        string? envPath = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        string[] candidates =
        [
            @"C:\Software\Ffmpeg\ffmpeg.exe",
            @"C:\Users\patri\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe",
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string FixtureVideoPath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "Fixtures",
            "live-session-repro.mp4"
        );

    [SkippableFact]
    public async Task KillingSessionMidEncode_DeletesScratchDirectory_NoFileHandleRace()
    {
        string? ffmpegPath = FindFfmpeg();
        string fixtureVideo = Path.GetFullPath(FixtureVideoPath());
        Skip.If(ffmpegPath is null, "No real ffmpeg found on this machine — skipping.");
        Skip.If(!File.Exists(fixtureVideo), $"Fixture video missing at {fixtureVideo}.");

        IStorage storage = TestStorageFactory.CreateLocal();
        ILiveSegmentInventory segmentInventory = TestStorageFactory.CreateSegmentInventory(storage);
        string cachePath = Path.Combine(Path.GetTempPath(), $"nomercy-killrace-{Guid.NewGuid():N}");

        EncoderOptions options = new()
        {
            FfmpegPathOverride = ffmpegPath,
            FfprobePathOverride = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe"),
            LiveTranscodeCachePath = cachePath,
            DefaultSegmentDurationSeconds = 2,
        };

        LiveQuality quality = new(
            Id: "480p",
            Label: "480p",
            Width: 320,
            Height: 240,
            Codec: VideoCodecType.H264,
            BitrateKbps: 800,
            Encoder: "libx264",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 1.0,
            CanRealtime: true
        );

        Mock<ILiveQualitySelector> selectorMock = new();
        selectorMock
            .Setup(s =>
                s.SelectOptimal(
                    It.IsAny<MediaInfo>(),
                    It.IsAny<ClientCapabilities>(),
                    It.IsAny<SpeedIndex>(),
                    It.IsAny<IResourceBudget>()
                )
            )
            .Returns(quality);

        SessionManager sessionManager = new(
            new() { MaxConcurrentSessions = 100, MaxSessionsPerUser = 100 }
        );
        LiveStreamingService streamingService = new(
            NullLogger<LiveStreamingService>.Instance,
            storage,
            segmentInventory
        );

        Mock<INvencSessionCap> nvencCap = new();
        IHardwareCapabilities hardware = new HardwareCapabilities([], Environment.ProcessorCount);
        ICodecResolver codecResolver = new CodecResolver(new CodecRegistry());
        Mock<IResourceBudget> budgetMock = new();
        ResourceLease lease = new("test", null, 0, 0);
        budgetMock
            .Setup(b =>
                b.AcquireAsync(It.IsAny<ResourceRequirement>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(lease);
        budgetMock.Setup(b => b.Acquire(It.IsAny<ResourceRequirement>())).Returns(lease);
        budgetMock.Setup(b => b.Release(It.IsAny<ResourceLease>()));

        ProcessRunner processRunner = new(NullLogger<ProcessRunner>.Instance);
        LiveFfmpegRunner runner = new(
            processRunner,
            options,
            NullLogger<LiveFfmpegRunner>.Instance,
            storage,
            nvencCap.Object,
            hardware,
            codecResolver,
            budgetMock.Object
        );

        SpeedIndex speedIndex = new(new());

        LiveEncoder encoder = new(
            selectorMock.Object,
            sessionManager,
            streamingService,
            runner,
            segmentInventory,
            options,
            speedIndex,
            budgetMock.Object,
            NullLogger<LiveEncoder>.Instance
        );

        MediaInfo media = new(
            FilePath: fixtureVideo,
            Format: "mov,mp4,m4a,3gp,3g2,mj2",
            Duration: TimeSpan.FromSeconds(6),
            OverallBitRateKbps: 800,
            FileSizeBytes: new FileInfo(fixtureVideo).Length,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "h264",
                    Width: 320,
                    Height: 240,
                    FrameRate: 15.0,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: "bt709",
                    ColorTransfer: "bt709",
                    ColorSpace: "bt709",
                    IsDefault: true,
                    BitRateKbps: 800
                ),
            ],
            AudioStreams:
            [
                new(
                    Index: 1,
                    Codec: "aac",
                    Channels: 2,
                    SampleRate: 44100,
                    BitRateKbps: 128,
                    Language: "eng",
                    IsDefault: true,
                    IsForced: false
                ),
            ],
            SubtitleStreams: [],
            Chapters: []
        );

        ClientCapabilities client = new(
            SupportedVideoCodecs: [VideoCodecType.H264],
            SupportedAudioCodecs: [AudioCodecType.Aac],
            SupportedContainers: ["mp4"],
            MaxWidth: 1920,
            MaxHeight: 1080,
            SupportsHdr: false,
            Supports10Bit: false,
            MaxBitrateKbps: 0
        );

        LiveEncodeRequest request = new(
            InputPath: fixtureVideo,
            CachedInfo: media,
            Client: client,
            StartPosition: TimeSpan.Zero,
            PreferredQuality: null
        );

        ILiveSession session = await encoder.StartAsync(request, CancellationToken.None);

        // Let ffmpeg actually start writing segments — mirrors the real-world
        // timing where a viewer kills playback a few seconds into a live
        // transcode, not the instant it starts.
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        string scratchDir = Path.Combine(cachePath, $"lts-{session.SessionId}");
        while (DateTime.UtcNow < deadline && !Directory.Exists(scratchDir))
            await Task.Delay(200);

        Assert.True(
            Directory.Exists(scratchDir),
            $"ffmpeg never created its scratch directory at {scratchDir} within 15s — real encode did not start."
        );

        // This is the exact call LiveStreamingService.RemoveAsync makes when a
        // client kills a session: cancel -> dispose (now awaits the runner
        // task) -> delete scratch. Assert the delete succeeds on the first try,
        // with no caught IOException, proving DisposeAsync actually waited for
        // ffmpeg's file handles to release.
        await streamingService.RemoveAsync(session.SessionId);

        Assert.False(
            Directory.Exists(scratchDir),
            $"Scratch directory {scratchDir} still exists after RemoveAsync — "
                + "the dispose-then-delete race reproduced (ffmpeg's file handles "
                + "were not released before cleanup ran)."
        );
    }
}
