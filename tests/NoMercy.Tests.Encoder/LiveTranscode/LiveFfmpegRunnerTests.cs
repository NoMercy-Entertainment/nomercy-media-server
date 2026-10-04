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
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.LiveTranscode;

public class LiveFfmpegRunnerTests
{
    private static INvencSessionCap NoopCap()
    {
        Mock<INvencSessionCap> m = new();
        return m.Object;
    }

    private static IHardwareCapabilities NoopHardware() =>
        new HardwareCapabilities([], Environment.ProcessorCount);

    private static IResourceBudget NoopBudget()
    {
        Mock<IResourceBudget> mock = new();
        ResourceLease lease = new("noop", null, 0, 0);
        mock.Setup(b => b.Acquire(It.IsAny<ResourceRequirement>())).Returns(lease);
        mock.Setup(b =>
                b.AcquireAsync(It.IsAny<ResourceRequirement>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(lease);
        mock.Setup(b => b.Release(It.IsAny<ResourceLease>()));
        return mock.Object;
    }

    private static ICodecResolver RealCodecResolver() => new CodecResolver(new CodecRegistry());

    private static LiveFfmpegRunner MakeRunner(IProcessRunner? processRunner = null) =>
        new(
            processRunner ?? new FakeProcessRunner(() => { }),
            new() { FfmpegPathOverride = "ffmpeg", FfprobePathOverride = "ffprobe" },
            NullLogger<LiveFfmpegRunner>.Instance,
            TestStorageFactory.CreateLocal(),
            NoopCap(),
            NoopHardware(),
            RealCodecResolver(),
            NoopBudget()
        );

    private static LiveQuality MakeQuality(
        int width = 1920,
        int height = 1080,
        int kbps = 8000,
        string encoder = "libx264"
    ) =>
        new(
            Id: $"{height}p",
            Label: $"{height}p",
            Width: width,
            Height: height,
            Codec: VideoCodecType.H264,
            BitrateKbps: kbps,
            Encoder: encoder,
            IsHardwareAccelerated: false,
            ExpectedSpeed: 2.0,
            CanRealtime: true
        );

    // ──────────────────────────────────────────────────────────────────────────
    // BuildArguments
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_IncludesCoreHlsFlags()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        args.Should().Contain("-f").And.Contain("hls");
        args.Should().Contain("-hls_time");
        args[Array.IndexOf(args, "-hls_time") + 1].Should().Be("4");
        args.Should().Contain("-hls_list_size");
        args[Array.IndexOf(args, "-hls_list_size") + 1].Should().Be("0");
        args.Should().Contain("-hls_playlist_type");
        args[Array.IndexOf(args, "-hls_playlist_type") + 1].Should().Be("event");
        args.Should().Contain(arg => arg.EndsWith("index.m3u8"));
    }

    [Fact]
    public void BuildArguments_OmitsSeek_WhenStartPositionIsZero()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 6
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        args.Should().NotContain("-ss");
        // The first run still numbers its segments from zero.
        args.Should().Contain("-start_number");
        args[Array.IndexOf(args, "-start_number") + 1].Should().Be("0");
    }

    [Fact]
    public void BuildArguments_SeekSnapsToSegmentBoundary_AndNumbersFromThatIndex()
    {
        // 120.5s with 6s segments falls inside absolute segment 20 (120–126s).
        // The input seek snaps to that segment's start and the muxer numbers its
        // first output segment 20, so seg_00020.ts maps 1:1 to the index hls.js
        // requests from the whole-runtime playlist. hls.js resolves the 0.5s
        // sub-segment offset by seeking inside the segment.
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.FromSeconds(120.5),
            Quality: MakeQuality(),
            SegmentDurationSeconds: 6
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int ssIdx = Array.IndexOf(args, "-ss");
        ssIdx.Should().BeGreaterThanOrEqualTo(0);
        args[ssIdx + 1].Should().Be("120.000");

        int startNumberIdx = Array.IndexOf(args, "-start_number");
        startNumberIdx.Should().BeGreaterThanOrEqualTo(0);
        args[startNumberIdx + 1].Should().Be("20");

        // Muxed PTS is shifted to the segment's true start so hls.js places the
        // seek fragment at 120s (20×6) instead of 0.
        int offsetIdx = Array.IndexOf(args, "-output_ts_offset");
        offsetIdx.Should().BeGreaterThanOrEqualTo(0);
        args[offsetIdx + 1].Should().Be("120.000");
    }

    [Fact]
    public void BuildArguments_UsesQualityEncoderAndBitrate()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(kbps: 3500, encoder: "h264_nvenc"),
            SegmentDurationSeconds: 4
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int vCodecIdx = Array.IndexOf(args, "-c:v");
        args[vCodecIdx + 1].Should().Be("h264_nvenc");

        int bvIdx = Array.IndexOf(args, "-b:v");
        args[bvIdx + 1].Should().Be("3500k");
    }

    [Fact]
    public void BuildArguments_UsesScaleFilterFromQualityDimensions()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(1280, 720, 4000),
            SegmentDurationSeconds: 4
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int vfIdx = Array.IndexOf(args, "-vf");
        args[vfIdx + 1].Should().Be("scale=1280:720,format=yuv420p");
    }

    [Fact]
    public void BuildArguments_IncludesProgressPipe_ForSpeedTracking()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 6
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int progIdx = Array.IndexOf(args, "-progress");
        progIdx.Should().BeGreaterThanOrEqualTo(0);
        args[progIdx + 1].Should().Be("pipe:1");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ParsePlaylist
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsePlaylist_ExtractsDurationAndIndexFromEachEntry()
    {
        string path = WritePlaylist(
            """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:0
            #EXTINF:6.000000,
            seg_00000.ts
            #EXTINF:6.000000,
            seg_00001.ts
            #EXTINF:4.040000,
            seg_00002.ts
            """
        );

        IReadOnlyList<(int Index, TimeSpan Duration)> entries = MakeRunner().ParsePlaylist(path);

        entries.Should().HaveCount(3);
        entries[0].Index.Should().Be(0);
        entries[0].Duration.Should().Be(TimeSpan.FromSeconds(6));
        entries[1].Index.Should().Be(1);
        entries[2].Index.Should().Be(2);
        entries[2].Duration.TotalSeconds.Should().BeApproximately(4.04, 0.001);
    }

    [Fact]
    public void ParsePlaylist_IgnoresComments_AndEndlist()
    {
        string path = WritePlaylist(
            """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:0
            #EXTINF:6.000000,
            seg_00000.ts
            #EXT-X-ENDLIST
            """
        );

        IReadOnlyList<(int Index, TimeSpan Duration)> entries = MakeRunner().ParsePlaylist(path);

        entries.Should().HaveCount(1);
        entries[0].Index.Should().Be(0);
    }

    [Fact]
    public void ParsePlaylist_MissingFile_ReturnsEmpty()
    {
        IReadOnlyList<(int Index, TimeSpan Duration)> entries = MakeRunner()
            .ParsePlaylist(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "index.m3u8")
            );

        entries.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RunAsync — integration with fake process runner + playlist file
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PushesSegmentsProducedDuringRun()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"live-runner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            LiveRunInput input = new(
                InputPath: "/media/in.mkv",
                OutputDirectory: tempDir,
                StartPosition: TimeSpan.Zero,
                Quality: MakeQuality(),
                SegmentDurationSeconds: 6
            );

            FakeProcessRunner runner = new(() =>
            {
                // Simulate FFmpeg producing two segments during its run
                File.WriteAllText(Path.Combine(tempDir, "seg_00000.ts"), new('a', 100));
                File.WriteAllText(Path.Combine(tempDir, "seg_00001.ts"), new('a', 100));
                File.WriteAllText(
                    Path.Combine(tempDir, "index.m3u8"),
                    """
                    #EXTM3U
                    #EXT-X-VERSION:3
                    #EXT-X-TARGETDURATION:6
                    #EXT-X-MEDIA-SEQUENCE:0
                    #EXTINF:6.000000,
                    seg_00000.ts
                    #EXTINF:6.000000,
                    seg_00001.ts
                    #EXT-X-ENDLIST
                    """
                );
            });

            LiveFfmpegRunner sut = new(
                runner,
                new() { FfmpegPathOverride = "ffmpeg", FfprobePathOverride = "ffprobe" },
                NullLogger<LiveFfmpegRunner>.Instance,
                TestStorageFactory.CreateLocal(),
                NoopCap(),
                NoopHardware(),
                RealCodecResolver(),
                NoopBudget()
            );

            LiveSession session = new("sess", MakeQuality());
            session.SetState(LiveSessionState.Transcoding);

            await sut.RunAsync(input, session, CancellationToken.None);

            List<Segment> received = [];
            await foreach (Segment segment in session.Segments)
            {
                received.Add(segment);
                if (received.Count >= 2)
                    break;
            }

            received.Should().HaveCount(2);
            received.Select(s => s.Index).Should().Equal(0, 1);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Conditional session-complete: a superseded (per-seek) runner must not
    // end the whole session's segment stream when its own ffmpeg exits.
    // ──────────────────────────────────────────────────────────────────────────

    private static async Task DrainAsync(LiveSession session)
    {
        await foreach (Segment _ in session.Segments) { }
    }

    [Fact]
    public async Task RunAsync_StillCurrentRunner_CompletesSessionSegmentChannel()
    {
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            $"live-runner-current-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(tempDir);

        try
        {
            LiveRunInput input = new(
                InputPath: "/media/in.mkv",
                OutputDirectory: tempDir,
                StartPosition: TimeSpan.Zero,
                Quality: MakeQuality(),
                SegmentDurationSeconds: 6
            );

            LiveFfmpegRunner sut = MakeRunner(new FakeProcessRunner(() => { }));
            LiveSession session = new("sess-current", MakeQuality());
            session.SetState(LiveSessionState.Transcoding);

            // RunAsync invoked with the session's OWN current runner token —
            // mirrors the real _runnerFactory wiring (LiveEncoder.SpawnRunner)
            // where no newer seek has superseded this runner.
            await sut.RunAsync(input, session, session.RunnerCancellation);

            Task drain = DrainAsync(session);
            Task completed = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(2)));

            completed.Should().Be(drain, "the current runner must complete the segment channel");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task RunAsync_SupersededBySeek_DoesNotCompleteSessionSegmentChannel()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"live-runner-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            LiveRunInput input = new(
                InputPath: "/media/in.mkv",
                OutputDirectory: tempDir,
                StartPosition: TimeSpan.Zero,
                Quality: MakeQuality(),
                SegmentDurationSeconds: 6
            );

            LiveFfmpegRunner sut = MakeRunner(new FakeProcessRunner(() => { }));
            LiveSession session = new("sess-stale", MakeQuality());
            session.SetState(LiveSessionState.Transcoding);

            CancellationToken staleToken = session.RunnerCancellation;

            // A seek/resume/quality-change swaps in a fresh runner CTS before
            // this (now-superseded) runner's ffmpeg process finishes. No
            // factory is attached, so the seek only replaces the CTS.
            await session.SeekAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

            await sut.RunAsync(input, session, staleToken);

            Task drain = DrainAsync(session);
            Task completed = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromMilliseconds(400)));

            completed
                .Should()
                .NotBe(drain, "a superseded runner must not end the session's segment stream");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Resource-lease lifetime: the lease is acquired before CreateDirectory /
    // AcquireLocalPath / BuildArguments run — a throw from any of those must
    // still release it, not leak the GPU/CPU budget forever.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ThrowFromCreateDirectory_StillReleasesResourceLease()
    {
        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.CreateDirectory(It.IsAny<string>()))
            .Throws(new IOException("disk full"));

        ResourceLease lease = new("lease-1", null, 0, 2);
        Mock<IResourceBudget> budget = new();
        budget
            .Setup(b =>
                b.AcquireAsync(It.IsAny<ResourceRequirement>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(lease);

        LiveFfmpegRunner sut = new(
            new FakeProcessRunner(() => { }),
            new() { FfmpegPathOverride = "ffmpeg", FfprobePathOverride = "ffprobe" },
            NullLogger<LiveFfmpegRunner>.Instance,
            storage.Object,
            NoopCap(),
            NoopHardware(),
            RealCodecResolver(),
            budget.Object
        );

        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 6
        );
        LiveSession session = new("sess-throw", MakeQuality());

        Func<Task> act = () => sut.RunAsync(input, session, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        budget.Verify(b => b.Release(lease), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Client capabilities: audio channels + HDR tonemap
    // ──────────────────────────────────────────────────────────────────────────

    private static ClientCapabilities MakeClientCaps(bool supportsHdr, int maxAudioChannels) =>
        new(
            SupportedVideoCodecs: [],
            SupportedAudioCodecs: [],
            SupportedContainers: [],
            MaxWidth: 1920,
            MaxHeight: 1080,
            SupportsHdr: supportsHdr,
            Supports10Bit: false,
            MaxBitrateKbps: 8000,
            MaxAudioChannels: maxAudioChannels
        );

    private static MediaInfo MakeMediaInfo(bool isHdr, int audioChannels)
    {
        VideoStreamInfo video = new(
            Index: 0,
            Codec: "hevc",
            Width: 3840,
            Height: 2160,
            FrameRate: 24,
            BitDepth: 10,
            PixelFormat: "yuv420p10le",
            ColorPrimaries: isHdr ? "bt2020" : "bt709",
            ColorTransfer: isHdr ? "smpte2084" : "bt709",
            ColorSpace: isHdr ? "bt2020nc" : "bt709",
            IsDefault: true,
            BitRateKbps: 40000
        );
        AudioStreamInfo audio = new(
            Index: 1,
            Codec: "truehd",
            Channels: audioChannels,
            SampleRate: 48000,
            BitRateKbps: 3000,
            Language: "eng",
            IsDefault: true,
            IsForced: false
        );
        return new(
            FilePath: "/media/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(120),
            OverallBitRateKbps: 43000,
            FileSizeBytes: 40_000_000_000L,
            VideoStreams: [video],
            AudioStreams: [audio],
            SubtitleStreams: [],
            Chapters: []
        );
    }

    [Fact]
    public void BuildArguments_HdrSource_SdrClient_AppendsTonemap()
    {
        // HDR source, client doesn't support HDR → tonemap chain must appear.
        LiveRunInput input = new(
            InputPath: "/media/hdr.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4,
            Client: MakeClientCaps(supportsHdr: false, maxAudioChannels: 2),
            SourceInfo: MakeMediaInfo(isHdr: true, audioChannels: 2)
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int vfIdx = Array.IndexOf(args, "-vf");
        string vf = args[vfIdx + 1];
        vf.Should().Contain("scale=");
        vf.Should().Contain("zscale=t=linear");
        vf.Should().Contain("tonemap=tonemap=hable");
        vf.Should().Contain("format=yuv420p");
    }

    [Fact]
    public void BuildArguments_HdrSource_HdrClient_NoTonemap()
    {
        // HDR source, client supports HDR → no tonemap.
        LiveRunInput input = new(
            InputPath: "/media/hdr.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4,
            Client: MakeClientCaps(supportsHdr: true, maxAudioChannels: 8),
            SourceInfo: MakeMediaInfo(isHdr: true, audioChannels: 8)
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int vfIdx = Array.IndexOf(args, "-vf");
        string vf = args[vfIdx + 1];
        vf.Should().NotContain("zscale");
        vf.Should().NotContain("tonemap");
    }

    [Fact]
    public void BuildArguments_AudioChannels_CappedByClientMax()
    {
        // Source has 7.1 (8 ch), client claims max 2 → -ac 2.
        LiveRunInput input = new(
            InputPath: "/media/surround.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4,
            Client: MakeClientCaps(supportsHdr: false, maxAudioChannels: 2),
            SourceInfo: MakeMediaInfo(isHdr: false, audioChannels: 8)
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int acIdx = Array.IndexOf(args, "-ac");
        args[acIdx + 1].Should().Be("2");
    }

    [Fact]
    public void BuildArguments_AudioChannels_UsesSourceWhenBelowClientMax()
    {
        // Source has stereo (2 ch), client claims max 8 → -ac 2 (don't upmix).
        LiveRunInput input = new(
            InputPath: "/media/stereo.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4,
            Client: MakeClientCaps(supportsHdr: false, maxAudioChannels: 8),
            SourceInfo: MakeMediaInfo(isHdr: false, audioChannels: 2)
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int acIdx = Array.IndexOf(args, "-ac");
        args[acIdx + 1].Should().Be("2");
    }

    [Fact]
    public void BuildArguments_NoClientCaps_DefaultsToStereo()
    {
        // No client caps → fallback to -ac 2.
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int acIdx = Array.IndexOf(args, "-ac");
        args[acIdx + 1].Should().Be("2");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Browser-safe 8-bit pixel format (2026-07 fix)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_TenBitSdrSource_ForcesYuv420p()
    {
        // 10-bit HEVC SDR source transcoded to H.264 for a browser client.
        // Before this fix, the non-tonemap path never forced a pixel format,
        // so libx264 auto-negotiated High-10 — a profile no browser decodes.
        LiveRunInput input = new(
            InputPath: "/media/sdr10bit.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(encoder: "libx264"),
            SegmentDurationSeconds: 4,
            Client: MakeClientCaps(supportsHdr: false, maxAudioChannels: 2),
            SourceInfo: MakeMediaInfo(isHdr: false, audioChannels: 2)
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int vfIdx = Array.IndexOf(args, "-vf");
        string vf = args[vfIdx + 1];
        vf.Should().NotContain("zscale");
        vf.Should().NotContain("tonemap");
        vf.Should().Contain("format=yuv420p");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GOP / keyframe alignment to HLS segment duration
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_Gop_ForceKeyFramesAndGopSizeTiedToSegmentDuration()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4,
            SourceInfo: MakeMediaInfo(isHdr: false, audioChannels: 2) // FrameRate: 24
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int fkfIdx = Array.IndexOf(args, "-force_key_frames");
        fkfIdx.Should().BeGreaterThanOrEqualTo(0);
        args[fkfIdx + 1].Should().Be("expr:gte(t,n_forced*4)");

        int gIdx = Array.IndexOf(args, "-g");
        gIdx.Should().BeGreaterThanOrEqualTo(0);
        args[gIdx + 1].Should().Be("192"); // 24fps * 4s segment * 2
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Rate control + preset
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_RateControlAndPreset_PresentForFullTranscode()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(kbps: 4000, encoder: "libx264"),
            SegmentDurationSeconds: 4
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int maxrateIdx = Array.IndexOf(args, "-maxrate");
        maxrateIdx.Should().BeGreaterThanOrEqualTo(0);
        args[maxrateIdx + 1].Should().Be("6000k");

        int bufsizeIdx = Array.IndexOf(args, "-bufsize");
        bufsizeIdx.Should().BeGreaterThanOrEqualTo(0);
        args[bufsizeIdx + 1].Should().Be("8000k");

        int presetIdx = Array.IndexOf(args, "-preset");
        presetIdx.Should().BeGreaterThanOrEqualTo(0);
        args[presetIdx + 1].Should().Be("veryfast");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Audio-only sources
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_AudioOnlySource_OmitsVideoMapAndAddsVn()
    {
        AudioStreamInfo audio = new(
            Index: 0,
            Codec: "flac",
            Channels: 2,
            SampleRate: 44100,
            BitRateKbps: 900,
            Language: "eng",
            IsDefault: true,
            IsForced: false
        );
        MediaInfo source = new(
            FilePath: "/media/audio-only.flac",
            Format: "flac",
            Duration: TimeSpan.FromMinutes(4),
            OverallBitRateKbps: 900,
            FileSizeBytes: 30_000_000,
            VideoStreams: [],
            AudioStreams: [audio],
            SubtitleStreams: [],
            Chapters: []
        );

        LiveRunInput input = new(
            InputPath: "/media/audio-only.flac",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4,
            SourceInfo: source
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        args.Should().NotContain("0:v:0");
        args.Should().Contain("-vn");
        int caIdx = Array.IndexOf(args, "-c:a");
        args[caIdx + 1].Should().Be("aac");
        args.Should().Contain("-f").And.Contain("hls");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // VideoOnly — audio comes from the file's own renditions via the master
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_VideoOnly_DropsAudioEntirely()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4,
            AudioStreamIndex: 1,
            VideoOnly: true
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        // Video is still mapped and encoded, but audio is dropped: no audio map,
        // no audio codec — just "-an". The master playlist supplies the audio.
        args.Should().Contain("0:v:0");
        args.Should().Contain("-an");
        args.Should().NotContain("-c:a");
        args.Should().NotContain(a => a.StartsWith("0:a:"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AudioRenditionOnly — one language transcoded to AAC, no video
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_AudioRenditionOnly_MapsOneLanguageToAac_NoVideo()
    {
        LiveRunInput input = new(
            InputPath: "/media/remux.mkv",
            OutputDirectory: "/tmp/live-audio-jpn",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(),
            SegmentDurationSeconds: 4,
            AudioStreamIndex: 2,
            AudioRenditionOnly: true
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        // No video is produced or mapped, and the selected language is transcoded
        // to AAC — this is the per-language rendition for a raw multi-audio source.
        args.Should().Contain("-vn");
        args.Should().NotContain("0:v:0");
        args.Should().NotContain("-c:v");
        args.Should().Contain("-map").And.Contain("0:a:2?");
        int caIdx = Array.IndexOf(args, "-c:a");
        args[caIdx + 1].Should().Be("aac");
        // Still an HLS output so it can be served and seeked like the video track.
        args.Should().Contain("-f").And.Contain("hls");
    }

    [Fact]
    public void BuildArguments_AudioRenditionOnly_SharesAbsoluteSegmentIndexingWithVideo()
    {
        // A language rendition must seek to the same segment boundaries the video
        // does so hls.js keeps audio and video aligned across a seek.
        LiveRunInput input = new(
            InputPath: "/media/remux.mkv",
            OutputDirectory: "/tmp/live-audio-eng",
            StartPosition: TimeSpan.FromSeconds(120.5),
            Quality: MakeQuality(),
            SegmentDurationSeconds: 6,
            AudioStreamIndex: 0,
            AudioRenditionOnly: true
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        args[Array.IndexOf(args, "-ss") + 1].Should().Be("120.000");
        args[Array.IndexOf(args, "-start_number") + 1].Should().Be("20");
        args[Array.IndexOf(args, "-output_ts_offset") + 1].Should().Be("120.000");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Remux vs full video transcode
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_RemuxDecision_UsesStreamCopyForVideoAndAudio()
    {
        VideoStreamInfo video = new(
            Index: 0,
            Codec: "h264",
            Width: 1920,
            Height: 1080,
            FrameRate: 24,
            BitDepth: 8,
            PixelFormat: "yuv420p",
            ColorPrimaries: "bt709",
            ColorTransfer: "bt709",
            ColorSpace: "bt709",
            IsDefault: true,
            BitRateKbps: 4500
        );
        AudioStreamInfo audio = new(
            Index: 1,
            Codec: "aac",
            Channels: 2,
            SampleRate: 48000,
            BitRateKbps: 128,
            Language: "eng",
            IsDefault: true,
            IsForced: false
        );
        MediaInfo source = new(
            FilePath: "/media/remux.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 5000,
            FileSizeBytes: 1_000_000_000,
            VideoStreams: [video],
            AudioStreams: [audio],
            SubtitleStreams: [],
            Chapters: []
        );
        ClientCapabilities client = new(
            SupportedVideoCodecs: [VideoCodecType.H264],
            SupportedAudioCodecs: [AudioCodecType.Aac],
            SupportedContainers: ["mp4"],
            MaxWidth: 1920,
            MaxHeight: 1080,
            SupportsHdr: true,
            Supports10Bit: true,
            MaxBitrateKbps: 0,
            MaxAudioChannels: 2
        );

        LiveRunInput input = new(
            InputPath: "/media/remux.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(encoder: "libx264"),
            SegmentDurationSeconds: 4,
            Client: client,
            SourceInfo: source
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int vIdx = Array.IndexOf(args, "-c:v");
        args[vIdx + 1].Should().Be("copy");
        int aIdx = Array.IndexOf(args, "-c:a");
        args[aIdx + 1].Should().Be("copy");
        args.Should().NotContain("-preset");
        args.Should().NotContain("-maxrate");
        args.Should().NotContain(a => a.StartsWith("scale="));
    }

    [Fact]
    public void BuildArguments_TranscodeVideoDecision_ReEncodesVideo()
    {
        // Client doesn't support the source's video codec at all → TranscodeVideo,
        // full re-encode using the resolved quality's encoder — never a copy.
        VideoStreamInfo video = new(
            Index: 0,
            Codec: "hevc",
            Width: 1920,
            Height: 1080,
            FrameRate: 24,
            BitDepth: 8,
            PixelFormat: "yuv420p",
            ColorPrimaries: "bt709",
            ColorTransfer: "bt709",
            ColorSpace: "bt709",
            IsDefault: true,
            BitRateKbps: 4500
        );
        MediaInfo source = new(
            FilePath: "/media/transcode.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 4500,
            FileSizeBytes: 1_000_000_000,
            VideoStreams: [video],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );
        ClientCapabilities client = new(
            SupportedVideoCodecs: [VideoCodecType.H264],
            SupportedAudioCodecs: [],
            SupportedContainers: ["mp4"],
            MaxWidth: 1920,
            MaxHeight: 1080,
            SupportsHdr: true,
            Supports10Bit: true,
            MaxBitrateKbps: 0,
            MaxAudioChannels: 2
        );

        LiveRunInput input = new(
            InputPath: "/media/transcode.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(encoder: "libx264"),
            SegmentDurationSeconds: 4,
            Client: client,
            SourceInfo: source
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int vIdx = Array.IndexOf(args, "-c:v");
        args[vIdx + 1].Should().Be("libx264");
        args.Should().Contain(a => a.StartsWith("scale="));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CustomArguments escape hatch
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_CustomArguments_AppearInFinalArgv_ButReservedFlagsAreSkipped()
    {
        LiveRunInput input = new(
            InputPath: "/media/in.mkv",
            OutputDirectory: "/tmp/live",
            StartPosition: TimeSpan.Zero,
            Quality: MakeQuality(kbps: 4000, encoder: "libx264"),
            SegmentDurationSeconds: 4,
            CustomArguments: new Dictionary<string, string>
            {
                ["-x264-params"] = "nal-hrd=cbr",
                ["-b:v"] = "999k", // reserved — must be skipped, not override the resolved -b:v
            }
        );

        string[] args = LiveFfmpegRunner.BuildArguments(input);

        int customIdx = Array.IndexOf(args, "-x264-params");
        customIdx.Should().BeGreaterThanOrEqualTo(0);
        args[customIdx + 1].Should().Be("nal-hrd=cbr");

        int bvIdx = Array.IndexOf(args, "-b:v");
        args[bvIdx + 1].Should().Be("4000k");
        args.Should().NotContain("999k");
    }

    private static string WritePlaylist(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"live-pl-{Guid.NewGuid():N}.m3u8");
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return path;
    }

    private sealed class FakeProcessRunner(Action onRun) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            string[] arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<ProcessResult> RunAsync(
            string executable,
            string[] arguments,
            Action<string>? onStdOut = null,
            Action<string>? onStdErr = null,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default
        )
        {
            onRun();
            return Task.FromResult(
                new ProcessResult(ExitCode: 0, StdOut: "", StdErr: "", Duration: TimeSpan.Zero)
            );
        }

        public Task<ProcessResult> RunAsync(
            string executable,
            string[] arguments,
            Action<string>? onStdOut,
            Action<string>? onStdErr,
            string? workingDirectory,
            CancellationToken cancellationToken,
            CancellationToken killSignal,
            Action<int>? onProcessStarted = null
        )
        {
            onRun();
            return Task.FromResult(
                new ProcessResult(ExitCode: 0, StdOut: "", StdErr: "", Duration: TimeSpan.Zero)
            );
        }

        public Task<ProcessResult> RunAsync(
            string executable,
            string[] arguments,
            IReadOnlyDictionary<string, string>? extraEnv,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new ProcessResult(ExitCode: 0, StdOut: "", StdErr: "", Duration: TimeSpan.Zero)
            );

        public Task<ProcessResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            Action<string>? onStdOut = null,
            Action<string>? onStdErr = null,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default,
            Action<int>? onProcessStarted = null
        ) => throw new NotImplementedException();
    }
}
