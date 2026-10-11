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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.LiveTranscode;

/// <summary>
/// End-to-end wiring proof for the seek-coverage fix: <see cref="LiveGapPlannerTests"/>
/// proves the planner in isolation and <see cref="LiveFfmpegArgumentBuilderStopPositionTests"/>
/// proves "-t" emission, but neither proves <see cref="LiveEncoder"/> actually
/// consults the on-disk segment inventory and feeds the plan into the runner it
/// spawns. These tests drive a real <see cref="LiveEncoder"/> + real
/// <see cref="LiveSegmentInventory"/> over a real scratch directory, capture the
/// exact <see cref="LiveRunInput"/> handed to the runner, and assert on it — if
/// this wiring were dropped, every other test in this slice would still pass and
/// the user's re-encode-on-seek bug would be unfixed.
/// </summary>
public class LiveEncoderGapFillTests
{
    private const int SegDur = 6;

    private static LiveQuality MakeQuality() =>
        new(
            Id: "1080p",
            Label: "1080p",
            Width: 1920,
            Height: 1080,
            Codec: VideoCodecType.H264,
            BitrateKbps: 8000,
            Encoder: "libx264",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 2.0,
            CanRealtime: true
        );

    private static MediaInfo MakeMedia(TimeSpan duration) =>
        new(
            FilePath: "/media/test.mkv",
            Format: "matroska,webm",
            Duration: duration,
            OverallBitRateKbps: 8000,
            FileSizeBytes: 5_000_000_000L,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "h264",
                    Width: 1920,
                    Height: 1080,
                    FrameRate: 24.0,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: "bt709",
                    ColorTransfer: "bt709",
                    ColorSpace: "bt709",
                    IsDefault: true,
                    BitRateKbps: 8000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static ClientCapabilities MakeClient() =>
        new(
            SupportedVideoCodecs: [VideoCodecType.H264],
            SupportedAudioCodecs: [AudioCodecType.Aac],
            SupportedContainers: ["mp4", "mkv"],
            MaxWidth: 1920,
            MaxHeight: 1080,
            SupportsHdr: false,
            Supports10Bit: false,
            MaxBitrateKbps: 0
        );

    private sealed class CapturingLiveFfmpegRunner : ILiveFfmpegRunner
    {
        private readonly ConcurrentQueue<LiveRunInput> _captured = new();

        public IReadOnlyList<LiveRunInput> Captured => [.. _captured];

        public Task RunAsync(LiveRunInput input, LiveSession session, CancellationToken ct)
        {
            _captured.Enqueue(input);
            return Task.CompletedTask;
        }
    }

    private sealed record Fixture(
        LiveEncoder Encoder,
        CapturingLiveFfmpegRunner Runner,
        IStorage Storage,
        string CachePath
    );

    private static Fixture BuildFixture()
    {
        IStorage storage = TestStorageFactory.CreateLocal();
        ILiveSegmentInventory segmentInventory = TestStorageFactory.CreateSegmentInventory(storage);
        string cachePath = Path.Combine(Path.GetTempPath(), $"nomercy-gapfill-{Guid.NewGuid():N}");

        EncoderOptions options = new()
        {
            FfmpegPathOverride = "ffmpeg",
            FfprobePathOverride = "ffprobe",
            LiveTranscodeCachePath = cachePath,
            DefaultSegmentDurationSeconds = SegDur,
        };

        Mock<ILiveQualitySelector> selectorMock = new();
        LiveQuality quality = MakeQuality();
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
        CapturingLiveFfmpegRunner runner = new();
        SpeedIndex speedIndex = new(new());
        IResourceBudget budget = new ResourceBudget(gpuDevices: [], cpuCores: 8);

        LiveEncoder encoder = new(
            selectorMock.Object,
            sessionManager,
            streamingService,
            runner,
            segmentInventory,
            options,
            speedIndex,
            budget,
            NullLogger<LiveEncoder>.Instance
        );

        return new(encoder, runner, storage, cachePath);
    }

    private static string ScratchDirFor(Fixture fixture, string sessionId) =>
        Path.Combine(fixture.CachePath, $"lts-{sessionId}");

    private static void PlantSegments(
        Fixture fixture,
        string scratchDir,
        int fromIndex,
        int toIndexInclusive
    )
    {
        fixture.Storage.CreateDirectory(scratchDir);
        for (int index = fromIndex; index <= toIndexInclusive; index++)
        {
            string path = fixture.Storage.CombinePath(scratchDir, $"seg_{index:D5}.ts");
            fixture.Storage.Write(path, [1]);
        }
    }

    private static async Task WaitForCaptureCountAsync(
        CapturingLiveFfmpegRunner runner,
        int expectedCount,
        TimeSpan timeout
    )
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (runner.Captured.Count >= expectedCount)
                return;
            await Task.Delay(10);
        }
    }

    private static async Task WaitForStateAsync(
        ILiveSession session,
        LiveSessionState expected,
        TimeSpan timeout
    )
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (session.State == expected)
                return;
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task SeekIntoGapBetweenTwoCoveredRanges_BoundsTheRespawn_InsteadOfReencodingToEof()
    {
        // The exact bug: covered 0..50 and 200..260, seek lands in the gap at
        // index 100. Without the fix the respawn is unbounded, re-encodes
        // 200..260 (content already on disk) and continues to EOF.
        Fixture fixture = BuildFixture();
        MediaInfo media = MakeMedia(TimeSpan.FromHours(1)); // lastIndex far beyond 260

        LiveEncodeRequest request = new(
            InputPath: "/media/test.mkv",
            CachedInfo: media,
            Client: MakeClient(),
            StartPosition: TimeSpan.Zero,
            PreferredQuality: null
        );

        ILiveSession session = await fixture.Encoder.StartAsync(request, CancellationToken.None);
        await WaitForCaptureCountAsync(fixture.Runner, 1, TimeSpan.FromSeconds(5));
        fixture
            .Runner.Captured.Should()
            .HaveCount(1, "the session-start spawn must have fired first");

        string scratchDir = ScratchDirFor(fixture, session.SessionId);
        PlantSegments(fixture, scratchDir, 0, 50);
        PlantSegments(fixture, scratchDir, 200, 260);

        await session.SeekAsync(TimeSpan.FromSeconds(100 * SegDur), CancellationToken.None);
        await WaitForCaptureCountAsync(fixture.Runner, 2, TimeSpan.FromSeconds(5));

        fixture.Runner.Captured.Should().HaveCount(2);
        LiveRunInput seekRun = fixture.Runner.Captured[1];
        seekRun.StartPosition.Should().Be(TimeSpan.FromSeconds(100 * SegDur));
        seekRun.StopPosition.Should().Be(TimeSpan.FromSeconds(200 * SegDur));
    }

    [Fact]
    public async Task SeekIntoAlreadyCoveredGround_SkipsForwardToTheRealGap_AndRunsToEof()
    {
        // Covered 0..50, seek to index 20 (already covered) — the respawn must
        // skip forward to the first real gap (51), not re-encode 20..50.
        Fixture fixture = BuildFixture();
        MediaInfo media = MakeMedia(TimeSpan.FromHours(1));

        LiveEncodeRequest request = new(
            InputPath: "/media/test.mkv",
            CachedInfo: media,
            Client: MakeClient(),
            StartPosition: TimeSpan.Zero,
            PreferredQuality: null
        );

        ILiveSession session = await fixture.Encoder.StartAsync(request, CancellationToken.None);
        await WaitForCaptureCountAsync(fixture.Runner, 1, TimeSpan.FromSeconds(5));

        string scratchDir = ScratchDirFor(fixture, session.SessionId);
        PlantSegments(fixture, scratchDir, 0, 50);

        await session.SeekAsync(TimeSpan.FromSeconds(20 * SegDur), CancellationToken.None);
        await WaitForCaptureCountAsync(fixture.Runner, 2, TimeSpan.FromSeconds(5));

        fixture.Runner.Captured.Should().HaveCount(2);
        LiveRunInput seekRun = fixture.Runner.Captured[1];
        seekRun.StartPosition.Should().Be(TimeSpan.FromSeconds(51 * SegDur));
        seekRun.StopPosition.Should().BeNull();
    }

    [Fact]
    public async Task SeekIntoFullyCoveredFile_SkipsTheSpawnEntirely_AndParksBuffered()
    {
        // The whole (short) file is already on disk — spawning would re-encode
        // content the client can already fetch. No new LiveRunInput must be
        // captured, and the session must park itself Buffered instead.
        Fixture fixture = BuildFixture();
        MediaInfo media = MakeMedia(TimeSpan.FromSeconds(60)); // lastIndex = 9 at 6s segments

        LiveEncodeRequest request = new(
            InputPath: "/media/test.mkv",
            CachedInfo: media,
            Client: MakeClient(),
            StartPosition: TimeSpan.Zero,
            PreferredQuality: null
        );

        ILiveSession session = await fixture.Encoder.StartAsync(request, CancellationToken.None);
        await WaitForCaptureCountAsync(fixture.Runner, 1, TimeSpan.FromSeconds(5));

        string scratchDir = ScratchDirFor(fixture, session.SessionId);
        PlantSegments(fixture, scratchDir, 0, 9); // the entire file, 0..lastIndex

        await session.SeekAsync(TimeSpan.FromSeconds(5 * SegDur), CancellationToken.None);
        await WaitForStateAsync(session, LiveSessionState.Buffered, TimeSpan.FromSeconds(5));

        fixture
            .Runner.Captured.Should()
            .HaveCount(1, "a fully-covered seek must not spawn a new run");
        session.State.Should().Be(LiveSessionState.Buffered);
    }

    [Fact]
    public async Task SeekRespawn_ForClientWithout10BitHevc_KeepsTheH264Transcode()
    {
        // Phone: HEVC decode tops out at 8-bit; source is 10-bit HEVC. The start
        // run decides TranscodeVideo (H.264 out). A seek respawn must build the
        // same video command, never a stream copy that passes Main10 through.
        Fixture fixture = BuildFixture();
        MediaInfo media = MakeMedia(TimeSpan.FromHours(1)) with
        {
            Format = "mpegts",
            VideoStreams =
            [
                new(
                    Index: 0,
                    Codec: "hevc",
                    Width: 1920,
                    Height: 1080,
                    FrameRate: 24.0,
                    BitDepth: 10,
                    PixelFormat: "yuv420p10le",
                    ColorPrimaries: "bt709",
                    ColorTransfer: "bt709",
                    ColorSpace: "bt709",
                    IsDefault: true,
                    BitRateKbps: 4000
                ),
            ],
        };
        ClientCapabilities phone = new(
            SupportedVideoCodecs: [VideoCodecType.H264, VideoCodecType.H265],
            SupportedAudioCodecs: [AudioCodecType.Aac],
            SupportedContainers: ["mp4", "ts"],
            MaxWidth: 1920,
            MaxHeight: 1080,
            SupportsHdr: false,
            Supports10Bit: false,
            MaxBitrateKbps: 0
        );

        LiveEncodeRequest request = new(
            InputPath: "/media/test.ts",
            CachedInfo: media,
            Client: phone,
            StartPosition: TimeSpan.Zero,
            PreferredQuality: null
        );

        ILiveSession session = await fixture.Encoder.StartAsync(request, CancellationToken.None);
        await WaitForCaptureCountAsync(fixture.Runner, 1, TimeSpan.FromSeconds(5));
        PlantSegments(fixture, ScratchDirFor(fixture, session.SessionId), 0, 5);

        await session.SeekAsync(TimeSpan.FromSeconds(100 * SegDur), CancellationToken.None);
        await WaitForCaptureCountAsync(fixture.Runner, 2, TimeSpan.FromSeconds(5));

        fixture.Runner.Captured.Should().HaveCount(2);
        foreach (LiveRunInput run in fixture.Runner.Captured)
        {
            string[] args = LiveFfmpegRunner.BuildArguments(run);
            int vIdx = Array.IndexOf(args, "-c:v");
            args[vIdx + 1].Should().Be("libx264");
            args.Should().Contain("-vf");
        }
    }
}
