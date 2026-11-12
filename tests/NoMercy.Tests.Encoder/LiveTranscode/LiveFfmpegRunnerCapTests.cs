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
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Encoder.LiveTranscode.Protocol;
using NoMercy.Resources;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.LiveTranscode;

public class LiveFfmpegRunnerCapTests
{
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

    private static LiveQuality MakeSwQuality() =>
        new(
            Id: "1080p-sw",
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

    private static LiveRunInput MakeInput(LiveQuality quality, string outputDir) =>
        new(
            InputPath: "/media/test.mkv",
            OutputDirectory: outputDir,
            StartPosition: TimeSpan.Zero,
            Quality: quality,
            SegmentDurationSeconds: 4
        );

    /// <summary>
    /// Builds a process runner mock that returns a successful result immediately.
    /// </summary>
    private static IProcessRunner MakeInstantProcessRunner()
    {
        ProcessResult ok = new(0, "", "", TimeSpan.Zero);
        Mock<IProcessRunner> mock = new();

        mock.Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ok);

        return mock.Object;
    }

    /// <summary>
    /// Builds a storage mock that satisfies the AcquireLocalPath + CreateDirectory calls
    /// made before FFmpeg is spawned, and returns "no playlist exists" so polling exits cleanly.
    /// </summary>
    private static IStorage MakeNoopStorage()
    {
        Mock<IStorage> mock = new();
        mock.Setup(s => s.CreateDirectory(It.IsAny<string>()));
        mock.Setup(s => s.AcquireLocalPath(It.IsAny<string>()))
            .Returns(new LocalPathLease(Path.GetTempPath()));
        mock.Setup(s => s.Exists(It.IsAny<string>())).Returns(false);
        return mock.Object;
    }

    private static LiveFfmpegRunner BuildRunner(
        INvencSessionCap cap,
        IHardwareCapabilities? hardware = null,
        IProcessRunner? processRunner = null,
        IStorage? storage = null,
        ICodecResolver? codecResolver = null,
        ILiveSessionTransport? transport = null
    )
    {
        IHardwareCapabilities hw =
            hardware ?? new HardwareCapabilities([], Environment.ProcessorCount);

        EncoderOptions opts = new()
        {
            FfmpegPathOverride = "ffmpeg",
            FfprobePathOverride = "ffprobe",
        };

        Mock<IResourceBudget> noopBudget = new();
        ResourceLease noopLease = new("noop", null, 0, 0);
        noopBudget.Setup(b => b.Acquire(It.IsAny<ResourceRequirement>())).Returns(noopLease);
        noopBudget
            .Setup(b =>
                b.AcquireAsync(It.IsAny<ResourceRequirement>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(noopLease);
        noopBudget.Setup(b => b.Release(It.IsAny<ResourceLease>()));

        return new(
            processRunner ?? MakeInstantProcessRunner(),
            opts,
            NullLogger<LiveFfmpegRunner>.Instance,
            storage ?? TestStorageFactory.CreateLocal(),
            cap,
            hw,
            codecResolver ?? new CodecResolver(new CodecRegistry()),
            noopBudget.Object,
            transport
        );
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Hardware encode — cap exhausted → falls back to software instead of
    // failing the session, and reports GpuFallbackToCpu on the transport.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_HwQuality_CapExhausted_FallsBackToSoftwareAndReportsGpuFallback()
    {
        Mock<INvencSessionCap> capMock = new();
        capMock
            .Setup(c => c.EnforceForGpuEncode(It.IsAny<string>(), true))
            .Throws(RuntimeErrors.GpuCapacityExhausted("RTX 3080", 3));

        List<(string SessionId, object Message)> pushed = [];
        Mock<ILiveSessionTransport> transportMock = new();
        transportMock
            .Setup(t =>
                t.SendToClientAsync(
                    It.IsAny<string>(),
                    It.IsAny<object>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, object, CancellationToken>(
                (sessionId, message, _) => pushed.Add((sessionId, message))
            )
            .Returns(Task.CompletedTask);

        LiveFfmpegRunner runner = BuildRunner(
            capMock.Object,
            processRunner: MakeInstantProcessRunner(),
            storage: MakeNoopStorage(),
            transport: transportMock.Object
        );

        LiveQuality hwQuality = MakeHwQuality();
        LiveSession session = new("cap-hw-001", hwQuality);
        string outputDir = Path.Combine(Path.GetTempPath(), "nomercy-cap-test-" + Ulid.NewUlid());

        Func<Task> act = () =>
            runner.RunAsync(MakeInput(hwQuality, outputDir), session, CancellationToken.None);

        await act.Should().NotThrowAsync();

        session.CurrentQuality.Encoder.Should().Be("libx264");
        session.CurrentQuality.IsHardwareAccelerated.Should().BeFalse();

        pushed.Should().ContainSingle(p => p.Message is QualityChangedMessage);
        QualityChangedMessage message = pushed
            .Select(p => p.Message)
            .OfType<QualityChangedMessage>()
            .Single();
        message.Reason.Should().Be(QualityChangeReason.GpuFallbackToCpu);
        message.NewQuality.Encoder.Should().Be("libx264");
    }

    // #646: the GPU lease wait is bounded; a viewer falls back to CPU.
    private static readonly TimeSpan LeaseDeadline = TimeSpan.FromSeconds(3);

    private static Mock<IResourceBudget> MakeBudget(bool gpuGrantable, bool cpuGrantable)
    {
        Mock<IResourceBudget> budget = new();
        int next = 0;
        budget
            .Setup(b =>
                b.AcquireAsync(It.IsAny<ResourceRequirement>(), It.IsAny<CancellationToken>())
            )
            .Returns(
                async (ResourceRequirement req, CancellationToken token) =>
                {
                    bool grantable = req.GpuSlots > 0 ? gpuGrantable : cpuGrantable;
                    if (!grantable)
                        await Task.Delay(Timeout.Infinite, token);

                    return new ResourceLease(
                        $"lease-{Interlocked.Increment(ref next)}",
                        req.GpuDeviceKey,
                        req.GpuSlots,
                        req.CpuThreads
                    );
                }
            );
        budget.Setup(b => b.Release(It.IsAny<ResourceLease>()));
        return budget;
    }

    private static LiveFfmpegRunner BuildBudgetRunner(
        IResourceBudget budget,
        IProcessRunner processRunner,
        ILiveSessionTransport transport
    ) =>
        new(
            processRunner,
            new EncoderOptions { FfmpegPathOverride = "ffmpeg", FfprobePathOverride = "ffprobe" },
            NullLogger<LiveFfmpegRunner>.Instance,
            MakeNoopStorage(),
            new Mock<INvencSessionCap>().Object,
            new HardwareCapabilities([], Environment.ProcessorCount),
            new CodecResolver(new CodecRegistry()),
            budget,
            transport
        );

    private static (Mock<ILiveSessionTransport> Mock, List<object> Pushed) MakeTransport()
    {
        List<object> pushed = [];
        Mock<ILiveSessionTransport> mock = new();
        mock.Setup(t =>
                t.SendToClientAsync(
                    It.IsAny<string>(),
                    It.IsAny<object>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, object, CancellationToken>((_, message, _) => pushed.Add(message))
            .Returns(Task.CompletedTask);
        return (mock, pushed);
    }

    [Fact]
    public async Task RunAsync_GpuLeaseNeverGranted_FallsBackToCpuAndHoldsCpuLease()
    {
        Mock<IResourceBudget> budget = MakeBudget(gpuGrantable: false, cpuGrantable: true);
        (Mock<ILiveSessionTransport> transport, List<object> pushed) = MakeTransport();
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

        LiveFfmpegRunner runner = BuildBudgetRunner(
            budget.Object,
            process.Object,
            transport.Object
        );
        LiveQuality hw = MakeHwQuality();
        LiveSession session = new("lease-fallback-001", hw);
        string dir = Path.Combine(Path.GetTempPath(), "lease-fb-" + Ulid.NewUlid());

        await runner
            .RunAsync(MakeInput(hw, dir), session, CancellationToken.None)
            .WaitAsync(LeaseDeadline);

        session.CurrentQuality.IsHardwareAccelerated.Should().BeFalse();
        pushed
            .OfType<QualityChangedMessage>()
            .Should()
            .ContainSingle(m => m.Reason == QualityChangeReason.GpuFallbackToCpu);
        budget.Verify(
            b =>
                b.AcquireAsync(
                    It.Is<ResourceRequirement>(r =>
                        r.GpuSlots == 0 && r.CpuThreads == EncodeThreadBudget.SoftwareEncode
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
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
            Times.Once
        );
        budget.Verify(
            b => b.Release(It.Is<ResourceLease>(l => l.GpuSlots == 0 && l.CpuThreads > 0)),
            Times.Once
        );
    }

    [Fact]
    public async Task RunAsync_NothingGrantable_SendsRecoverableResourceExhausted()
    {
        Mock<IResourceBudget> budget = MakeBudget(gpuGrantable: false, cpuGrantable: false);
        (Mock<ILiveSessionTransport> transport, List<object> pushed) = MakeTransport();
        Mock<IProcessRunner> process = new();

        LiveFfmpegRunner runner = BuildBudgetRunner(
            budget.Object,
            process.Object,
            transport.Object
        );
        LiveQuality hw = MakeHwQuality();
        LiveSession session = new("lease-exhausted-001", hw);
        string dir = Path.Combine(Path.GetTempPath(), "lease-ex-" + Ulid.NewUlid());

        await runner
            .RunAsync(MakeInput(hw, dir), session, CancellationToken.None)
            .WaitAsync(LeaseDeadline);

        TranscodeErrorMessage error = pushed.OfType<TranscodeErrorMessage>().Single();
        error.Kind.Should().Be(EncodingErrorKind.ResourceExhausted);
        error.Recoverable.Should().BeTrue();
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

    [Fact]
    public async Task RunAsync_GpuLeaseGrantedAtOnce_KeepsGpuEncodeWithoutFallback()
    {
        Mock<IResourceBudget> budget = MakeBudget(gpuGrantable: true, cpuGrantable: true);
        (Mock<ILiveSessionTransport> transport, List<object> pushed) = MakeTransport();

        LiveFfmpegRunner runner = BuildBudgetRunner(
            budget.Object,
            MakeInstantProcessRunner(),
            transport.Object
        );
        LiveQuality hw = MakeHwQuality();
        LiveSession session = new("lease-gpu-001", hw);
        string dir = Path.Combine(Path.GetTempPath(), "lease-gpu-" + Ulid.NewUlid());

        await runner
            .RunAsync(MakeInput(hw, dir), session, CancellationToken.None)
            .WaitAsync(LeaseDeadline);

        session.CurrentQuality.IsHardwareAccelerated.Should().BeTrue();
        pushed.Should().BeEmpty();
        budget.Verify(
            b =>
                b.AcquireAsync(
                    It.Is<ResourceRequirement>(r =>
                        r.GpuSlots == 1
                        && r.CpuThreads == EncodeThreadBudget.GpuEncodeWithCpuFilters
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        budget.Verify(b => b.Release(It.Is<ResourceLease>(l => l.GpuSlots == 1)), Times.Once);
    }

    private static ResourceBudget MakeRealBudget(int gpuSlots) =>
        new(
            [new GpuDevice(GpuVendor.Nvidia, "GPU", 8192, gpuSlots, [VideoCodecType.H264])],
            cpuCores: 32
        );

    private static LiveFfmpegRunner BuildRealBudgetRunner(
        ResourceBudget budget,
        List<ResourceLease> grantedLeases
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
            .Returns(() =>
            {
                grantedLeases.Add(new("probe", null, 0, 32 - budget.AvailableCpuThreads()));
                return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
            });
        (Mock<ILiveSessionTransport> transport, _) = MakeTransport();
        return BuildBudgetRunner(budget, process.Object, transport.Object);
    }

    [Fact]
    public async Task RunAsync_QueueHoldsCpuAndEveryGpuSlot_LiveViewerStillGetsCpuLease()
    {
        ResourceBudget budget = MakeRealBudget(gpuSlots: 1);
        budget.Acquire(new ResourceRequirement("GPU", 1, 0));
        budget.Acquire(new ResourceRequirement(null, 0, 31));
        List<ResourceLease> seen = [];
        LiveFfmpegRunner runner = BuildRealBudgetRunner(budget, seen);
        LiveQuality hw = MakeHwQuality();
        LiveSession session = new("lease-overcommit-001", hw);
        string dir = Path.Combine(Path.GetTempPath(), "lease-oc-" + Ulid.NewUlid());

        await runner
            .RunAsync(MakeInput(hw, dir), session, CancellationToken.None)
            .WaitAsync(LeaseDeadline);

        session.State.Should().NotBe(LiveSessionState.Error);
        session.CurrentQuality.IsHardwareAccelerated.Should().BeFalse();
        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_QueueHoldsCpuAndAGpuSlotIsFree_LiveViewerGetsGpuLease()
    {
        ResourceBudget budget = MakeRealBudget(gpuSlots: 2);
        budget.Acquire(new ResourceRequirement("GPU", 1, 0));
        budget.Acquire(new ResourceRequirement(null, 0, 31));
        List<ResourceLease> seen = [];
        LiveFfmpegRunner runner = BuildRealBudgetRunner(budget, seen);
        LiveQuality hw = MakeHwQuality();
        LiveSession session = new("lease-overcommit-002", hw);
        string dir = Path.Combine(Path.GetTempPath(), "lease-oc-" + Ulid.NewUlid());

        await runner
            .RunAsync(MakeInput(hw, dir), session, CancellationToken.None)
            .WaitAsync(LeaseDeadline);

        session.CurrentQuality.IsHardwareAccelerated.Should().BeTrue();
        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task AcquireAsync_InteractiveOverCommit_ReleaseRestoresExactCpuCount()
    {
        ResourceBudget budget = MakeRealBudget(gpuSlots: 1);
        ResourceLease queue = budget.Acquire(new ResourceRequirement(null, 0, 31));
        budget.AvailableCpuThreads().Should().Be(1);

        ResourceLease live = await budget.AcquireAsync(
            new ResourceRequirement(null, 0, 8, Interactive: true)
        );
        budget.AvailableCpuThreads().Should().Be(0);

        budget.Release(live);
        budget.AvailableCpuThreads().Should().Be(1);
        budget.Release(queue);
        budget.AvailableCpuThreads().Should().Be(32);
    }

    [Fact]
    public async Task AcquireAsync_InteractiveOverCommit_QueueTryAcquireSeesLiveLoad()
    {
        ResourceBudget budget = MakeRealBudget(gpuSlots: 1);
        ResourceLease queue = budget.Acquire(new ResourceRequirement(null, 0, 31));
        ResourceLease live = await budget.AcquireAsync(
            new ResourceRequirement(null, 0, 8, Interactive: true)
        );
        budget.Release(queue);

        budget.AvailableCpuThreads().Should().Be(24);
        budget.TryAcquire(new ResourceRequirement(null, 0, 25), TimeSpan.Zero).Should().BeNull();
        ResourceLease? fits = budget.TryAcquire(
            new ResourceRequirement(null, 0, 24),
            TimeSpan.Zero
        );
        fits.Should().NotBeNull();

        budget.Release(fits!);
        budget.Release(live);
        budget.AvailableCpuThreads().Should().Be(32);
    }

    [Fact]
    public void TryAcquire_NoLiveSession_BehavesAsBefore()
    {
        ResourceBudget budget = MakeRealBudget(gpuSlots: 1);
        budget.Acquire(new ResourceRequirement(null, 0, 31));

        budget.TryAcquire(new ResourceRequirement(null, 0, 2), TimeSpan.Zero).Should().BeNull();
        budget.TryAcquire(new ResourceRequirement(null, 0, 1), TimeSpan.Zero).Should().NotBeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Software encode — EnforceForGpuEncode called with requiresGpu=false
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SwQuality_EnforceCalledWithRequiresGpuFalse()
    {
        Mock<INvencSessionCap> capMock = new();

        LiveFfmpegRunner runner = BuildRunner(
            capMock.Object,
            processRunner: MakeInstantProcessRunner(),
            storage: MakeNoopStorage()
        );

        LiveSession session = new("cap-sw-001", MakeSwQuality());
        string outputDir = Path.Combine(Path.GetTempPath(), "cap-sw-" + Ulid.NewUlid());

        await runner.RunAsync(
            MakeInput(MakeSwQuality(), outputDir),
            session,
            CancellationToken.None
        );

        // Must be called with requiresGpu=false for software encodes.
        capMock.Verify(c => c.EnforceForGpuEncode(It.IsAny<string>(), false), Times.Once);
        capMock.Verify(c => c.EnforceForGpuEncode(It.IsAny<string>(), true), Times.Never);
    }
}
