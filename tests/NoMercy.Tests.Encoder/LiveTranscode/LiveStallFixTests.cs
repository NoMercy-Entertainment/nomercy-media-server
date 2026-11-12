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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Resources;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Hardware;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.LiveTranscode;

public class LiveStallFixTests
{
    private static readonly ResourceLease Granted = new("lease", null, 0, 0);

    private static LiveQuality MakeHwQuality() =>
        new(
            Id: "1080p",
            Label: "1080p",
            Width: 1920,
            Height: 1080,
            Codec: VideoCodecType.H264,
            BitrateKbps: 8000,
            Encoder: "h264_nvenc",
            IsHardwareAccelerated: true,
            ExpectedSpeed: 5.0,
            CanRealtime: true
        );

    private static LiveQuality MakeLowQuality() =>
        MakeHwQuality() with
        {
            Id = "720p",
            Label = "720p",
            Width = 1280,
            Height = 720,
        };

    private static MediaInfo MakeSource(string format, params string[] audioCodecs) =>
        new(
            FilePath: "/media/test.file",
            Format: format,
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 5000,
            FileSizeBytes: 1_000_000_000,
            VideoStreams:
            [
                new(
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
                ),
            ],
            AudioStreams:
            [
                .. audioCodecs.Select(
                    (codec, i) =>
                        new AudioStreamInfo(
                            Index: i + 1,
                            Codec: codec,
                            Channels: 2,
                            SampleRate: 48000,
                            BitRateKbps: 128,
                            Language: "eng",
                            IsDefault: i == 0,
                            IsForced: false
                        )
                ),
            ],
            SubtitleStreams: [],
            Chapters: []
        );

    private static ClientCapabilities MakeAacClient(string container) =>
        new(
            SupportedVideoCodecs: [VideoCodecType.H264],
            SupportedAudioCodecs: [AudioCodecType.Aac],
            SupportedContainers: [container],
            MaxWidth: 1920,
            MaxHeight: 1080,
            SupportsHdr: true,
            Supports10Bit: true,
            MaxBitrateKbps: 0,
            MaxAudioChannels: 2
        );

    private static LiveRunInput MakeInput(
        LiveQuality quality,
        MediaInfo? source = null,
        ClientCapabilities? client = null,
        int audioStreamIndex = 0
    ) =>
        new(
            InputPath: "/media/test.file",
            OutputDirectory: Path.Combine(Path.GetTempPath(), "stall-" + Ulid.NewUlid()),
            StartPosition: TimeSpan.Zero,
            Quality: quality,
            SegmentDurationSeconds: 4,
            Client: client,
            SourceInfo: source,
            AudioStreamIndex: audioStreamIndex
        );

    private sealed class CapturingLogger : ILogger<LiveFfmpegRunner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (LiveFfmpegRunner Runner, Mock<IProcessRunner> Process) BuildRunner(
        IResourceBudget budget,
        ILogger<LiveFfmpegRunner>? logger = null,
        INvencSessionCap? cap = null,
        TimeSpan? leaseWaitTimeout = null
    )
    {
        Mock<IProcessRunner> process = new();
        process
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.Zero));

        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.AcquireLocalPath(It.IsAny<string>()))
            .Returns(new LocalPathLease(Path.GetTempPath()));
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(false);

        LiveFfmpegRunner runner = new(
            process.Object,
            new() { FfmpegPathOverride = "ffmpeg", FfprobePathOverride = "ffprobe" },
            logger ?? NullLogger<LiveFfmpegRunner>.Instance,
            storage.Object,
            cap ?? Mock.Of<INvencSessionCap>(),
            new HardwareCapabilities([], Environment.ProcessorCount),
            new CodecResolver(new CodecRegistry()),
            budget
        )
        {
            LeaseWaitTimeout = leaseWaitTimeout ?? TimeSpan.FromSeconds(30),
        };
        return (runner, process);
    }

    private static Mock<IResourceBudget> NeverGrantingBudget()
    {
        Mock<IResourceBudget> budget = new();
        budget
            .Setup(b =>
                b.AcquireAsync(It.IsAny<ResourceRequirement>(), It.IsAny<CancellationToken>())
            )
            .Returns(
                async (ResourceRequirement _, CancellationToken token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return Granted;
                }
            );
        return budget;
    }

    private static Mock<IResourceBudget> GrantingBudget(List<ResourceRequirement> asked)
    {
        Mock<IResourceBudget> budget = new();
        budget
            .Setup(b =>
                b.AcquireAsync(It.IsAny<ResourceRequirement>(), It.IsAny<CancellationToken>())
            )
            .Callback<ResourceRequirement, CancellationToken>((req, _) => asked.Add(req))
            .ReturnsAsync(Granted);
        return budget;
    }

    // Item 1 (caller): a Remux that copies audio must copy a client-playable stream.
    [Fact]
    public void Remux_CopiesAudio_OnlyWhenSelectedStreamIsClientPlayable()
    {
        MediaInfo source = MakeSource("matroska,webm", "eac3", "aac");
        ClientCapabilities client = MakeAacClient("mp4");

        string[] firstStream = LiveFfmpegRunner.BuildArguments(
            MakeInput(MakeHwQuality(), source, client, audioStreamIndex: 0)
        );
        string[] secondStream = LiveFfmpegRunner.BuildArguments(
            MakeInput(MakeHwQuality(), source, client, audioStreamIndex: 1)
        );

        firstStream[Array.IndexOf(firstStream, "-c:a") + 1].Should().Be("aac");
        secondStream[Array.IndexOf(secondStream, "-c:a") + 1].Should().Be("copy");
    }

    // Item 2: the lease wait is bounded and ends in a reported session error.
    [Fact]
    public async Task RunAsync_BudgetNeverGrants_FailsWithinBoundAndReportsError()
    {
        Mock<IResourceBudget> budget = NeverGrantingBudget();
        (LiveFfmpegRunner runner, Mock<IProcessRunner> process) = BuildRunner(
            budget.Object,
            leaseWaitTimeout: TimeSpan.FromMilliseconds(200)
        );
        LiveSession session = new("stall-001", MakeHwQuality());

        Task run = runner.RunAsync(MakeInput(MakeHwQuality()), session, CancellationToken.None);

        Func<Task> act = () => run.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().NotThrowAsync();

        session.State.Should().Be(LiveSessionState.Error);
        process.Verify(
            r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    // Item 2: a caller cancel is still a cancel, not a reported error.
    [Fact]
    public async Task RunAsync_BudgetNeverGrants_CallerCancel_IsNotReportedAsError()
    {
        Mock<IResourceBudget> budget = NeverGrantingBudget();
        (LiveFfmpegRunner runner, _) = BuildRunner(budget.Object);
        LiveSession session = new("stall-002", MakeHwQuality());
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        Func<Task> act = () => runner.RunAsync(MakeInput(MakeHwQuality()), session, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        session.State.Should().NotBe(LiveSessionState.Error);
    }

    // Item 2: a run that copies video reserves no GPU slot and no 8-thread share.
    [Fact]
    public async Task RunAsync_TranscodeAudioCopiesVideo_AsksNoGpuSlot()
    {
        List<ResourceRequirement> asked = [];
        Mock<INvencSessionCap> cap = new();
        (LiveFfmpegRunner runner, _) = BuildRunner(GrantingBudget(asked).Object, cap: cap.Object);
        LiveSession session = new("stall-003", MakeHwQuality());
        LiveRunInput input = MakeInput(
            MakeHwQuality(),
            MakeSource("hls,applehttp", "eac3"),
            MakeAacClient("hls")
        );

        await runner.RunAsync(input, session, CancellationToken.None);

        asked.Should().ContainSingle();
        asked[0].GpuSlots.Should().Be(0);
        asked[0].CpuThreads.Should().BeLessThan(EncodeThreadBudget.GpuEncodeWithCpuFilters);
        cap.Verify(c => c.EnforceForGpuEncode(It.IsAny<string>(), true), Times.Never);
    }

    // Item 2: offline-equivalent behavior is unchanged for a real video encode.
    [Fact]
    public async Task RunAsync_VideoReencode_StillAsksGpuSlotAndThreadShare()
    {
        List<ResourceRequirement> asked = [];
        (LiveFfmpegRunner runner, _) = BuildRunner(GrantingBudget(asked).Object);
        LiveSession session = new("stall-004", MakeHwQuality());

        await runner.RunAsync(MakeInput(MakeHwQuality()), session, CancellationToken.None);

        asked.Should().ContainSingle();
        asked[0].GpuSlots.Should().Be(1);
        asked[0].CpuThreads.Should().Be(EncodeThreadBudget.GpuEncodeWithCpuFilters);
    }

    // Item 4: one Information line before and one after the wait, with session id and lease.
    [Fact]
    public async Task RunAsync_LogsLeaseWaitBeforeAndAfter_AtInformation()
    {
        CapturingLogger logger = new();
        (LiveFfmpegRunner runner, _) = BuildRunner(GrantingBudget([]).Object, logger);
        LiveSession session = new("stall-005", MakeHwQuality());

        await runner.RunAsync(MakeInput(MakeHwQuality()), session, CancellationToken.None);

        List<string> lines =
        [
            .. logger
                .Entries.Where(e =>
                    e.Level == LogLevel.Information && e.Message.Contains("resource lease")
                )
                .Select(e => e.Message),
        ];
        lines.Should().HaveCount(2);
        lines.Should().OnlyContain(l => l.Contains("stall-005"));
        lines[0].Should().Contain("GpuSlots=1");
    }

    // Item 3: no segment produced yet means a quality drop cannot help.
    [Fact]
    public async Task BufferAdaptive_NoSegmentProduced_DoesNotDropQuality()
    {
        NoMercy.Storage.IStorage storage = TestStorageFactory.CreateLocal();
        LiveStreamingService streamingService = new(
            NullLogger<LiveStreamingService>.Instance,
            storage,
            TestStorageFactory.CreateSegmentInventory(storage)
        );
        LiveQuality high = MakeHwQuality();
        LiveSession session = new(Ulid.NewUlid().ToString(), high);
        session.SetState(LiveSessionState.Transcoding);
        streamingService.Register(session, TimeSpan.FromSeconds(4));
        streamingService.StampRequestContext(
            session.SessionId,
            MakeSource("hls", "aac"),
            MakeAacClient("hls")
        );

        Mock<ILiveQualitySelector> selector = new();
        selector
            .Setup(s =>
                s.GetAvailableQualities(
                    It.IsAny<MediaInfo>(),
                    It.IsAny<ClientCapabilities>(),
                    It.IsAny<SpeedIndex>(),
                    It.IsAny<IResourceBudget>()
                )
            )
            .Returns([high, MakeLowQuality()]);
        session.AttachRunnerFactory((_, _) => Task.CompletedTask);

        LiveSessionLimits limits = new();
        BufferAdaptiveService service = new(
            streamingService,
            selector.Object,
            new BufferManager(limits),
            new StubSpeedIndexProvider(),
            Mock.Of<IResourceBudget>(),
            limits,
            NullLogger<BufferAdaptiveService>.Instance
        );

        await service.EvaluateAllAsync(CancellationToken.None);

        session.CurrentQuality.Id.Should().Be("1080p");
    }
}
