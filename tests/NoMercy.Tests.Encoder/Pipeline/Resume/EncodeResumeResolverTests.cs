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
using NoMercy.Encoder.BuildingBlocks.Drm;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Resume;
using NoMercy.Encoder.Profiles;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Pipeline.Resume;

/// <summary>
/// The resume point comes from counting segment files; every refusal starts at zero.
/// </summary>
public class EncodeResumeResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"resume_resolver_{Guid.NewGuid():N}"
    );
    private readonly IStorage _storage = TestStorageFactory.CreateLocal();
    private readonly FakeSegmentProber _prober = new();
    private readonly CapturingLogger _logger = new();
    private readonly EncodeResumeMarker _marker;
    private readonly EncodeResumeResolver _resolver;

    public EncodeResumeResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "video"));
        Directory.CreateDirectory(Path.Combine(_dir, "audio"));
        _marker = new(_storage);
        _resolver = new(_storage, _prober, _marker, _logger);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Resolve_CountsContiguousSegments_DropsTheLastAndTakesTheShortestOutput()
    {
        // video 0..5 (6 files, last one partial -> 5 complete), audio 0..7 (-> 7 complete)
        Segments("video", "v", 6);
        Segments("audio", "a", 8);
        _prober.Set("video/v_00000.ts", 1.4);
        _prober.Set("video/v_00005.ts", 10.408);
        ResumeRequest request = Request(StartMarker(DefaultCommands()));

        ResumeDecision decision = await _resolver.ResolveAsync(request, default);

        decision.Resume.Should().BeTrue(decision.Reason);
        decision.StartSegment.Should().Be(5);
        decision.ResumeMs.Should().Be(9008);
    }

    [Fact]
    public async Task Resolve_ResumedEncodeStoppedByTheHost_ResumesAgainFromTheLaterSegment()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 8);
        _prober.Set("video/v_00000.ts", 1.4);
        _prober.Set("video/v_00005.ts", 10.408);
        FfmpegCommand[] commands = StartMarker(DefaultCommands());
        ResumeDecision first = await _resolver.ResolveAsync(Request(commands), default);
        first.StartSegment.Should().Be(5);

        // The resumed run wrote segments 6..9 before the host stopped it; the marker stays.
        Segments("video", "v", 10);
        Segments("audio", "a", 11);
        _prober.Set("video/v_00009.ts", 18.018);

        ResumeDecision second = await _resolver.ResolveAsync(Request(commands), default);

        second.Resume.Should().BeTrue(second.Reason);
        second.StartSegment.Should().Be(9);
        second.ResumeMs.Should().Be(16618);
        File.Exists(Path.Combine(_dir, "video", "v_00000.ts")).Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_PartialLastSegment_IsNotCounted()
    {
        Segments("video", "v", 2);
        Segments("audio", "a", 2);
        _prober.Set("video/v_00000.ts", 0);
        _prober.Set("video/v_00001.ts", 2.002);

        ResumeDecision decision = await _resolver.ResolveAsync(
            Request(StartMarker(DefaultCommands())),
            default
        );

        decision.StartSegment.Should().Be(1);
        decision.ResumeMs.Should().Be(2002);
    }

    [Fact]
    public async Task Resolve_GapInSegmentNumbers_StopsAtTheGap()
    {
        Segments("video", "v", 3);
        File.WriteAllBytes(Path.Combine(_dir, "video", "v_00004.ts"), [1]);
        Segments("audio", "a", 9);
        _prober.Set("video/v_00000.ts", 0);
        _prober.Set("video/v_00002.ts", 4.004);

        ResumeDecision decision = await _resolver.ResolveAsync(
            Request(StartMarker(DefaultCommands())),
            default
        );

        decision.StartSegment.Should().Be(2);
    }

    [Fact]
    public async Task Resolve_OnlyOneSegment_StartsAtZero()
    {
        Segments("video", "v", 1);
        Segments("audio", "a", 1);

        ResumeDecision decision = await _resolver.ResolveAsync(
            Request(StartMarker(DefaultCommands())),
            default
        );

        decision.Resume.Should().BeFalse();
        decision.Reason.Should().Contain("segment");
    }

    [Fact]
    public async Task Resolve_FinishedPlaylist_StartsAtZero()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        File.WriteAllText(Path.Combine(_dir, "video", "v.m3u8"), "#EXTM3U\n#EXT-X-ENDLIST\n");

        ResumeDecision decision = await _resolver.ResolveAsync(
            Request(StartMarker(DefaultCommands())),
            default
        );

        decision.Resume.Should().BeFalse();
        decision.Reason.Should().Contain("finished");
    }

    [Fact]
    public async Task Resolve_SecondPassOfATwoPassEncode_StartsAtZero() =>
        await AssertRefused(r => r with { Pass = EncodingPass.Two }, "pass");

    [Fact]
    public async Task Resolve_DurationLimit_StartsAtZero() =>
        await AssertRefused(
            r => r with { DurationLimit = TimeSpan.FromMinutes(1) },
            "duration limit"
        );

    [Fact]
    public async Task Resolve_CheckpointSeekAlreadySet_StartsAtZero() =>
        await AssertRefused(r => r with { ResumeFromMs = 5000 }, "seek");

    [Fact]
    public async Task Resolve_BurnInSubtitles_StartsAtZero() =>
        await AssertRefused(
            r =>
                r with
                {
                    Plan = r.Plan with
                    {
                        SubtitleOutputs =
                        [
                            new SubtitleOutputPlan(
                                SubtitleCodecType.Ass,
                                StreamAction.Transcode,
                                "en",
                                0,
                                null,
                                Policy: SubtitlePolicy.BurnIn
                            ),
                        ],
                    },
                },
            "burn-in"
        );

    [Fact]
    public async Task Resolve_CopyVideo_StartsAtZero() =>
        await AssertRefused(
            r =>
                r with
                {
                    Plan = r.Plan with
                    {
                        VideoOutputs = [r.Plan.VideoOutputs[0] with { EncoderName = "copy" }],
                    },
                },
            "copy"
        );

    [Fact]
    public async Task Resolve_CopyAudio_StartsAtZero() =>
        await AssertRefused(
            r =>
                r with
                {
                    Plan = r.Plan with
                    {
                        AudioOutputs = [r.Plan.AudioOutputs[0] with { Action = StreamAction.Copy }],
                    },
                },
            "copy"
        );

    [Fact]
    public async Task Resolve_DrmPlan_StartsAtZero() =>
        await AssertRefused(
            r =>
                r with
                {
                    Plan = r.Plan with { Drm = new(DrmMethod.Aes128, "https://keys.invalid/k") },
                },
            "drm"
        );

    [Fact]
    public async Task Resolve_NonHlsPlan_StartsAtZero() =>
        await AssertRefused(
            r => r with { Plan = r.Plan with { Format = OutputFormat.Mp4 } },
            "hls"
        );

    [Fact]
    public async Task Resolve_MarkerMissing_StartsAtZeroAndWritesTheMarker()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        ResumeRequest request = Request(DefaultCommands());

        ResumeDecision decision = await _resolver.ResolveAsync(request, default);

        decision.Resume.Should().BeFalse();
        decision.Reason.Should().Contain("marker");
        File.Exists(MarkerPath(request)).Should().BeTrue();
        File.ReadAllText(MarkerPath(request)).Should().StartWith("corr-1\nscope:");
    }

    [Fact]
    public async Task Resolve_MarkerOfOtherArguments_StartsAtZero()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        File.WriteAllText(Path.Combine(_dir, ".resume." + new string('0', 64)), "old");

        ResumeDecision decision = await _resolver.ResolveAsync(Request(DefaultCommands()), default);

        decision.Resume.Should().BeFalse();
        decision.Reason.Should().Contain("marker");
    }

    [Fact]
    public void Resolve_ChangedArgumentList_ChangesTheMarkerHash()
    {
        string first = EncodeResumeMarker.HashArguments(DefaultCommands());
        FfmpegCommand[] changed = DefaultCommands();
        changed[0] = changed[0] with { Arguments = [.. changed[0].Arguments, "-crf", "18"] };

        EncodeResumeMarker.HashArguments(changed).Should().NotBe(first);
        EncodeResumeMarker.HashArguments(DefaultCommands()).Should().Be(first);
    }

    [Fact]
    public async Task Resolve_ProbeFails_StartsAtZeroAndReplacesTheMarker()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        ResumeRequest request = Request(StartMarker(DefaultCommands(), "old-run"));
        _prober.Set("video/v_00000.ts", 0);

        ResumeDecision decision = await _resolver.ResolveAsync(request, default);

        decision.Resume.Should().BeFalse();
        decision.Reason.Should().Contain("probe");
        File.ReadAllText(MarkerPath(request)).Should().StartWith("corr-1\nscope:");
    }

    [Fact]
    public async Task Resolve_ProbeThrows_StartsAtZeroAndReplacesTheMarker()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        ResumeRequest request = Request(StartMarker(DefaultCommands(), "old-run"));
        _prober.Throw = true;

        ResumeDecision decision = await _resolver.ResolveAsync(request, default);

        decision.Resume.Should().BeFalse();
        decision.Reason.Should().Contain("failed");
        File.ReadAllText(MarkerPath(request)).Should().StartWith("corr-1\nscope:");
    }

    [Fact]
    public async Task Resolve_SegmentFileGone_StartsAtZero()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        _prober.Set("video/v_00000.ts", 0);
        _prober.Set("video/v_00005.ts", 0);

        ResumeDecision decision = await _resolver.ResolveAsync(
            Request(StartMarker(DefaultCommands())),
            default
        );

        decision.Resume.Should().BeFalse();
        decision.Reason.Should().Contain("not after");
    }

    [Fact]
    public async Task Resolve_ReadsTheFirstVideoOutput_ForTheResumeTime()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        _prober.Set("video/v_00000.ts", 1.4);
        _prober.Set("video/v_00005.ts", 10.408);

        await _resolver.ResolveAsync(Request(StartMarker(DefaultCommands())), default);

        _prober.Calls.Should().Equal(("video/v_00000.ts", true), ("video/v_00005.ts", true));
    }

    [Fact]
    public async Task Resolve_AudioOnlyPlan_ReadsTheFirstAudioOutput()
    {
        Segments("audio", "a", 6);
        _prober.Set("audio/a_00000.ts", 0);
        _prober.Set("audio/a_00005.ts", 10.0);
        FfmpegCommand[] commands = [AudioOnlyCommand()];
        ResumeRequest request = Request(StartMarker(commands)) with
        {
            Plan = PlanWith() with { VideoOutputs = [] },
        };

        ResumeDecision decision = await _resolver.ResolveAsync(request, default);

        decision.Resume.Should().BeTrue(decision.Reason);
        _prober.Calls.Should().Equal(("audio/a_00000.ts", false), ("audio/a_00005.ts", false));
    }

    [Fact]
    public async Task Resolve_BFrameVideo_CarriesTheStartSegmentDelay()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        _prober.Set("video/v_00000.ts", 1.48);
        _prober.Set("video/v_00005.ts", 11.48);
        _prober.Delay = 0.08;

        ResumeDecision decision = await _resolver.ResolveAsync(
            Request(StartMarker(DefaultCommands())),
            default
        );

        decision.ResumeMs.Should().Be(10000);
        decision.TimestampDelayMs.Should().Be(80);
    }

    [Fact]
    public async Task Resolve_DelayCannotBeProbed_StartsAtZero()
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        _prober.Set("video/v_00000.ts", 1.4);
        _prober.Set("video/v_00005.ts", 11.4);
        _prober.Delay = null;

        ResumeDecision decision = await _resolver.ResolveAsync(
            Request(StartMarker(DefaultCommands())),
            default
        );

        decision.Resume.Should().BeFalse();
        decision.Reason.Should().Contain("probe");
    }

    [Fact]
    public void ApplyTo_TimestampOffset_IsTheResumeTimePlusTheDelay()
    {
        ResumeDecision decision = new(true, 5, 10000, "resuming", null, 80);

        OutputPlan plan = EncodeResumeResolver.ApplyTo(PlanWith(), decision);

        plan.VideoOutputs[0].ExtraFlags!["-output_ts_offset"].Should().Be("10.080");
        plan.VideoOutputs[0].ExtraFlags!["-start_number"].Should().Be("5");
        plan.AudioOutputs[0].ExtraFlags!["-start_number"].Should().Be("5");
    }

    [Theory]
    [InlineData("libfdk_aac", 12000, 48000, "12.053333")] // 563 frames of 21.333 ms, plus two frames of delay
    [InlineData("libfdk_aac", 10000, 48000, "10.048000")] // 469 frames, plus two
    [InlineData("libfdk_aac", 10000, 44100, "10.054240")] // 431 frames of 23.220 ms, plus two
    [InlineData("aac", 12000, 48000, "12.032000")] // 563 frames, plus one
    public void ApplyTo_AacAudio_StartsOnTheFrameGridAfterTheResumeTime(
        string encoder,
        long resumeMs,
        int sampleRate,
        string expected
    )
    {
        OutputPlan plan = PlanWith();
        plan = plan with
        {
            AudioOutputs =
            [
                plan.AudioOutputs[0] with
                {
                    EncoderName = encoder,
                    SampleRate = sampleRate,
                },
            ],
        };

        OutputPlan applied = EncodeResumeResolver.ApplyTo(
            plan,
            new(true, 5, resumeMs, "resuming", null, 80)
        );

        applied.AudioOutputs[0].ExtraFlags!["-output_ts_offset"].Should().Be(expected);
    }

    [Fact]
    public void ApplyTo_NonAacAudio_StartsAtTheResumeTime()
    {
        OutputPlan plan = PlanWith();
        plan = plan with { AudioOutputs = [plan.AudioOutputs[0] with { EncoderName = "libopus" }] };

        OutputPlan applied = EncodeResumeResolver.ApplyTo(
            plan,
            new(true, 5, 12000, "resuming", null, 80)
        );

        applied.AudioOutputs[0].ExtraFlags!["-output_ts_offset"].Should().Be("12.000000");
    }

    [Fact]
    public async Task Resolve_LogsOneLineWithTheReason_WhenItStartsAtZero()
    {
        Segments("video", "v", 1);

        await _resolver.ResolveAsync(Request(StartMarker(DefaultCommands())), default);

        _logger.Lines.Should().ContainSingle().Which.Should().Contain("starts at 0");
    }

    [Fact]
    public async Task Resolve_StartOver_DeletesTheSegmentsAndMarkersOfAnOlderRun()
    {
        // An older server was stopped at segment 100 and left no marker.
        Segments("video", "v", 101);
        Segments("audio", "a", 101);
        string keep = Path.Combine(_dir, "video", "keep_00001.ts");
        File.WriteAllBytes(keep, [1]);
        string otherMarker = Path.Combine(_dir, ".resume." + new string('0', 64));
        File.WriteAllText(otherMarker, "old");
        ResumeRequest request = Request(DefaultCommands());

        ResumeDecision first = await _resolver.ResolveAsync(request, default);

        first.Resume.Should().BeFalse();
        Directory.GetFiles(Path.Combine(_dir, "video"), "v_*.ts").Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_dir, "audio"), "a_*.ts").Should().BeEmpty();
        File.Exists(keep).Should().BeTrue("only files of the segment pattern go");
        File.Exists(otherMarker).Should().BeFalse();
        File.Exists(MarkerPath(request)).Should().BeTrue();

        // The new run writes segments 0..10 and is stopped; none of the old ones may count.
        Segments("video", "v", 11);
        Segments("audio", "a", 11);
        _prober.Set("video/v_00000.ts", 0);
        _prober.Set("video/v_00010.ts", 20.02);

        ResumeDecision second = await _resolver.ResolveAsync(request, default);

        second.Resume.Should().BeTrue(second.Reason);
        second.StartSegment.Should().Be(10);
    }

    [Fact]
    public async Task Resolve_LoudnormAudio_StartsAtZero() =>
        await AssertRefused(
            r =>
                r with
                {
                    Plan = r.Plan with
                    {
                        AudioOutputs =
                        [
                            r.Plan.AudioOutputs[0] with
                            {
                                AudioFilter = "loudnorm=I=-16:TP=-1.5:LRA=11",
                            },
                        ],
                    },
                },
            "loudnorm"
        );

    [Fact]
    public void HashArguments_ReplacedSourceFile_ChangesTheHash()
    {
        string source = Path.Combine(_dir, "in.mkv");
        File.WriteAllBytes(source, [1, 2, 3]);
        FfmpegCommand[] commands = [new("ffmpeg", ["-i", source, "-f", "hls"], null)];
        string first = EncodeResumeMarker.HashArguments(commands);

        File.WriteAllBytes(source, [1, 2, 3, 4]);
        string longer = EncodeResumeMarker.HashArguments(commands);
        File.SetLastWriteTimeUtc(source, new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string touched = EncodeResumeMarker.HashArguments(commands);

        longer.Should().NotBe(first);
        touched.Should().NotBe(longer);
    }

    [Fact]
    public async Task Resolve_MarkerCannotBeWritten_StartsAtZeroWithoutThrowing()
    {
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where the working folder should be");
        ResumeRequest request = Request(DefaultCommands()) with
        {
            OutputDirectory = Path.Combine(blocker, "work"),
        };

        ResumeDecision decision = await _resolver.ResolveAsync(request, default);

        decision.Resume.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAll_RemovesStitchTempFiles_AndKeepsThePlaylist()
    {
        string playlist = Path.Combine(_dir, "video", "v.m3u8");
        string stitch = playlist + ".stitch";
        File.WriteAllText(playlist, "#EXTM3U");
        File.WriteAllText(stitch, "#EXTM3U");

        await _marker.ClearAllAsync(_dir, default);

        File.Exists(stitch).Should().BeFalse();
        File.Exists(playlist).Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_StartAtZero_KeepsTheMarkerAndStitchFilesOfAnotherBundle()
    {
        FfmpegCommand[] audio = [AudioOnlyCommand()];
        FfmpegCommand[] video = [VideoOnlyCommand("-crf")];
        FfmpegCommand[] oldVideo = [VideoOnlyCommand("-preset")];
        await _resolver.ResolveAsync(Request(audio), default);
        await _resolver.ResolveAsync(Request(oldVideo), default);
        string audioMarker = MarkerPath(Request(audio));
        string oldVideoMarker = MarkerPath(Request(oldVideo));
        string audioStitch = Path.Combine(_dir, "audio", "a.m3u8.stitch");
        string videoStitch = Path.Combine(_dir, "video", "v.m3u8.stitch");
        File.WriteAllText(audioStitch, "#EXTM3U");
        File.WriteAllText(videoStitch, "#EXTM3U");

        await _resolver.ResolveAsync(Request(video), default);

        File.Exists(audioMarker).Should().BeTrue("another bundle's resume must survive");
        File.Exists(audioStitch).Should().BeTrue();
        File.Exists(oldVideoMarker)
            .Should()
            .BeFalse("a marker of this bundle's old arguments goes");
        File.Exists(videoStitch).Should().BeFalse();
        File.Exists(MarkerPath(Request(video))).Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_InputThatIsNotAFilePath_StartsAtZeroWithoutThrowing()
    {
        FfmpegCommand[] commands =
        [
            new(
                "ffmpeg",
                [
                    "-i",
                    "http://media.example/in.mkv",
                    "-f",
                    "hls",
                    "-hls_segment_filename",
                    "video/v_%05d.ts",
                    "video/v.m3u8",
                ],
                null
            ),
        ];

        ResumeDecision decision = await _resolver.ResolveAsync(Request(commands), default);

        decision.Resume.Should().BeFalse();
    }

    private static FfmpegCommand VideoOnlyCommand(string extraFlag) =>
        new(
            "ffmpeg",
            [
                "-i",
                "in.mkv",
                extraFlag,
                "1",
                "-f",
                "hls",
                "-hls_segment_filename",
                "video/v_%05d.ts",
                "video/v.m3u8",
            ],
            null
        );

    private async Task AssertRefused(Func<ResumeRequest, ResumeRequest> change, string reason)
    {
        Segments("video", "v", 6);
        Segments("audio", "a", 6);
        _prober.Set("video/v_00000.ts", 0);
        _prober.Set("video/v_00005.ts", 10.0);
        ResumeRequest request = change(Request(StartMarker(DefaultCommands())));

        ResumeDecision decision = await _resolver.ResolveAsync(request, default);

        decision.Resume.Should().BeFalse();
        decision.Reason.ToLowerInvariant().Should().Contain(reason);
    }

    private void Segments(string folder, string name, int count)
    {
        for (int i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(_dir, folder, $"{name}_{i:D5}.ts"), [1]);
    }

    private FfmpegCommand[] StartMarker(FfmpegCommand[] commands, string startedBy = "run-0")
    {
        string path = _marker.PathFor(_dir, EncodeResumeMarker.HashArguments(commands));
        File.WriteAllText(path, startedBy);
        return commands;
    }

    private string MarkerPath(ResumeRequest request) =>
        _marker.PathFor(_dir, EncodeResumeMarker.HashArguments(request.Commands));

    private static FfmpegCommand[] DefaultCommands() =>
        [
            new(
                "ffmpeg",
                [
                    "-i",
                    "in.mkv",
                    "-f",
                    "hls",
                    "-hls_segment_filename",
                    "video/v_%05d.ts",
                    "video/v.m3u8",
                    "-f",
                    "hls",
                    "-hls_segment_filename",
                    "audio/a_%05d.ts",
                    "audio/a.m3u8",
                ],
                null
            ),
        ];

    private static FfmpegCommand AudioOnlyCommand() =>
        new(
            "ffmpeg",
            [
                "-i",
                "in.mkv",
                "-f",
                "hls",
                "-hls_segment_filename",
                "audio/a_%05d.ts",
                "audio/a.m3u8",
            ],
            null
        );

    private ResumeRequest Request(FfmpegCommand[] commands) =>
        new(PlanWith(), commands, _dir, EncodingPass.Single, null, null, "corr-1");

    private static OutputPlan PlanWith() =>
        new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    Width: 1920,
                    Height: 1080,
                    EncoderName: "libx264",
                    Crf: 23,
                    BitrateKbps: 4000,
                    Preset: "medium",
                    Profile: "high",
                    Level: "4.1",
                    TenBit: false,
                    PixelFormat: "yuv420p",
                    MapLabel: "[v0]",
                    ExtraFlags: new()
                ),
            ],
            AudioOutputs:
            [
                new(
                    EncoderName: "aac",
                    BitrateKbps: 192,
                    Channels: 2,
                    SampleRate: 48000,
                    Action: StreamAction.Transcode,
                    Language: "en",
                    MapLabel: "0:a:0"
                ),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

    private sealed class FakeSegmentProber : ISegmentProber
    {
        private readonly Dictionary<string, double> _pts = [];
        public List<(string Path, bool Video)> Calls { get; } = [];
        public bool Throw { get; set; }

        public void Set(string relativePath, double seconds) => _pts[relativePath] = seconds;

        public Task<double?> FirstPacketPtsAsync(
            string segmentPath,
            bool video,
            CancellationToken ct
        )
        {
            if (Throw)
                throw new IOException("probe crashed");

            string relative = segmentPath.Replace('\\', '/');
            relative = relative[(relative.LastIndexOf('/', relative.LastIndexOf('/') - 1) + 1)..];
            Calls.Add((relative, video));
            return Task.FromResult(_pts.TryGetValue(relative, out double v) ? v : (double?)null);
        }

        public Task<double?> FirstPtsAsync(string segmentPath, CancellationToken ct) =>
            FirstPacketPtsAsync(segmentPath, true, ct);

        public double? Delay { get; set; } = 0;

        public Task<double?> FirstPacketDelayAsync(
            string segmentPath,
            bool video,
            CancellationToken ct
        ) => Task.FromResult(Delay);
    }

    private sealed class CapturingLogger : ILogger<EncodeResumeResolver>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add(formatter(state, exception));
    }
}
