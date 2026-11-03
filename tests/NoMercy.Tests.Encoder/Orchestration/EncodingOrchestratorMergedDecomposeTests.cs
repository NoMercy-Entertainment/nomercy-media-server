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
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Orchestration;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Strategies;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using Container = NoMercy.Encoder.Profiles.Container;

namespace NoMercy.Tests.Encoder.Orchestration;

/// <summary>
/// The smart-orchestrator "unify multiple presets into one coordinated
/// encode" seam: <see cref="EncodingOrchestrator.DecomposeMergedAsync"/> and
/// <see cref="EncodingOrchestrator.PlanMergedAsync"/>. Covers the three
/// acceptance criteria from the slice spec:
///   (a) merging two profiles yields one plan with both video renditions —
///       proven at the <c>OutputPlanMerger</c> layer (see
///       <c>OutputPlanMergerTests</c>); this file proves the orchestrator
///       wires that merge into a single <c>strategy.Decompose</c> call.
///   (b) collision guard — covered by <c>OutputPlanMergerTests</c>.
///   (c) a single preset merges to an identical plan as before — proven here
///       by asserting <c>DecomposeMergedAsync</c> with one request produces
///       the exact same tasks as <c>DecomposeAsync</c>.
/// Plus the fallback contract: incompatible presets (different container
/// formats, different encode modes, a plan failure) throw
/// <see cref="MergedEncodingIncompatibleException"/> rather than silently
/// producing a wrong merge — the caller (VideoEncodeJob) is expected to catch
/// this and fall back to independent per-preset dispatch.
/// </summary>
public class EncodingOrchestratorMergedDecomposeTests
{
    private const string GroupTag = "01HZMERGED0000000000000";

    private readonly Mock<IStrategyResolver> _resolver = new();
    private readonly Mock<IStorage> _storage = new();
    private readonly Mock<IEncoder> _encoder = new();

    public EncodingOrchestratorMergedDecomposeTests()
    {
        _storage
            .Setup(s => s.AcquireLocalPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => new(path));
        _storage.Setup(s => s.Driver).Returns(new LocalStorageDriver());
    }

    private EncodingOrchestrator BuildOrchestrator() =>
        new(
            _resolver.Object,
            _storage.Object,
            _encoder.Object,
            NullLogger<EncodingOrchestrator>.Instance
        );

    private static EncodingRequest BuildRequest(string presetName, Container container) =>
        new(
            InputPath: "/media/test.mkv",
            OutputDirectory: "out",
            Profile: new(
                Id: Ulid.NewUlid(),
                Name: presetName,
                Container: container,
                Video: null,
                Audio: [],
                Subtitles: []
            )
        );

    private static VideoOutputPlan MakeVideo(int width, int height, bool isHdrOutput) =>
        new(
            Width: width,
            Height: height,
            EncoderName: "libx265",
            Crf: 23,
            BitrateKbps: 0,
            Preset: "medium",
            Profile: "main",
            Level: "4.0",
            TenBit: false,
            PixelFormat: "yuv420p",
            MapLabel: "[v0]",
            ExtraFlags: [],
            VideoRange: isHdrOutput ? VideoRange.Pq : VideoRange.Sdr
        );

    private static OutputPlan MakePlan(
        OutputFormat format,
        int width,
        int height,
        bool isHdrOutput
    ) =>
        new(
            Format: format,
            VideoOutputs: [MakeVideo(width, height, isHdrOutput)],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null
        );

    // ------------------------------------------------------------------
    // (c) single request merges to an identical plan / task set as before.
    // ------------------------------------------------------------------

    [Fact]
    public async Task DecomposeMergedAsync_SingleRequest_MatchesDecomposeAsync()
    {
        EncodingRequest request = BuildRequest("Solo", Container.HlsTs);
        OutputPlan plan = MakePlan(OutputFormat.Hls, 1920, 1080, isHdrOutput: false);

        _encoder
            .Setup(e => e.PlanAsync(It.IsAny<EncodingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        Mock<IEncodingStrategy> strategy = new();
        strategy.Setup(s => s.Format).Returns(OutputFormat.Hls);
        strategy.Setup(s => s.EncodeMode).Returns(EncodeMode.SinglePass);
        strategy
            .Setup(s => s.Decompose(It.IsAny<OutputPlan>(), It.IsAny<string>()))
            .Returns(
                (OutputPlan p, string tag) =>
                    [
                        new DecomposedTask(
                            TaskId: $"{tag}-video-0",
                            ParentJobId: 0,
                            GroupTag: tag,
                            Kind: EncodeTaskKind.Video,
                            OutputIndex: 0,
                            Resources: null,
                            Label: $"{p.VideoOutputs[0].Width}p"
                        ),
                    ]
            );
        _resolver
            .Setup(r => r.Resolve(OutputFormat.Hls, EncodeMode.SinglePass))
            .Returns(strategy.Object);

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        DecomposedTask[] viaDecomposeAsync = await orchestrator.DecomposeAsync(request, GroupTag);
        DecomposedTask[] viaMerged = await orchestrator.DecomposeMergedAsync([request], GroupTag);

        viaMerged.Should().BeEquivalentTo(viaDecomposeAsync);
    }

    // ------------------------------------------------------------------
    // Multi-preset merge wires ONE merged plan into ONE Decompose call.
    // ------------------------------------------------------------------

    [Fact]
    public async Task DecomposeMergedAsync_TwoPresets_CallsDecomposeOnceWithMergedPlan()
    {
        EncodingRequest fourKRequest = BuildRequest("4K HDR HEVC", Container.HlsTs);
        EncodingRequest sdrRequest = BuildRequest("1080p SDR HEVC", Container.HlsTs);

        OutputPlan fourKPlan = MakePlan(OutputFormat.Hls, 3840, 2160, isHdrOutput: true);
        OutputPlan sdrPlan = MakePlan(OutputFormat.Hls, 1920, 1080, isHdrOutput: false);

        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "4K HDR HEVC"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(fourKPlan);
        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "1080p SDR HEVC"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(sdrPlan);

        OutputPlan? capturedPlan = null;
        Mock<IEncodingStrategy> strategy = new();
        strategy.Setup(s => s.Format).Returns(OutputFormat.Hls);
        strategy.Setup(s => s.EncodeMode).Returns(EncodeMode.SinglePass);
        strategy
            .Setup(s => s.Decompose(It.IsAny<OutputPlan>(), It.IsAny<string>()))
            .Returns(
                (OutputPlan p, string tag) =>
                {
                    capturedPlan = p;
                    return
                    [
                        new DecomposedTask($"{tag}-video-0", 0, tag, EncodeTaskKind.Video, 0, null),
                        new DecomposedTask($"{tag}-video-1", 0, tag, EncodeTaskKind.Video, 1, null),
                    ];
                }
            );
        _resolver
            .Setup(r => r.Resolve(OutputFormat.Hls, EncodeMode.SinglePass))
            .Returns(strategy.Object);

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        DecomposedTask[] tasks = await orchestrator.DecomposeMergedAsync(
            [fourKRequest, sdrRequest],
            GroupTag
        );

        strategy.Verify(
            s => s.Decompose(It.IsAny<OutputPlan>(), GroupTag),
            Times.Once,
            "one coordinated encode means exactly one Decompose call, not one per preset"
        );
        capturedPlan.Should().NotBeNull();
        capturedPlan!.VideoOutputs.Should().HaveCount(2);
        capturedPlan.VideoOutputs.Should().Contain(v => v.Width == 3840 && v.IsHdrOutput);
        capturedPlan.VideoOutputs.Should().Contain(v => v.Width == 1920 && !v.IsHdrOutput);
        tasks.Should().HaveCount(2);
    }

    // ------------------------------------------------------------------
    // Fallback contract: incompatible presets never silently produce a
    // wrong merge — they throw so the caller can fall back per-preset.
    // ------------------------------------------------------------------

    [Fact]
    public async Task DecomposeMergedAsync_DifferentContainers_Throws()
    {
        EncodingRequest hlsRequest = BuildRequest("HLS preset", Container.HlsTs);
        EncodingRequest mkvRequest = BuildRequest("MKV preset", Container.Mkv);

        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "HLS preset"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(MakePlan(OutputFormat.Hls, 1920, 1080, isHdrOutput: false));
        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "MKV preset"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(MakePlan(OutputFormat.Mkv, 1920, 1080, isHdrOutput: false));

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        Func<Task> act = async () =>
            await orchestrator.DecomposeMergedAsync([hlsRequest, mkvRequest], GroupTag);

        await act.Should().ThrowAsync<MergedEncodingIncompatibleException>();
    }

    [Fact]
    public async Task DecomposeMergedAsync_DifferentEncodeModes_Throws()
    {
        EncodingRequest singlePass = BuildRequest("Single", Container.HlsTs) with
        {
            Profile = new(
                Id: Ulid.NewUlid(),
                Name: "Single",
                Container: Container.HlsTs,
                Video: null,
                Audio: [],
                Subtitles: [],
                EncodeMode: EncodeMode.SinglePass
            ),
        };
        EncodingRequest twoPass = BuildRequest("TwoPass", Container.HlsTs) with
        {
            Profile = new(
                Id: Ulid.NewUlid(),
                Name: "TwoPass",
                Container: Container.HlsTs,
                Video: null,
                Audio: [],
                Subtitles: [],
                EncodeMode: EncodeMode.TwoPass
            ),
        };

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        Func<Task> act = async () =>
            await orchestrator.DecomposeMergedAsync([singlePass, twoPass], GroupTag);

        await act.Should().ThrowAsync<MergedEncodingIncompatibleException>();
    }

    [Fact]
    public async Task DecomposeMergedAsync_OnePresetFailsToPlan_Throws()
    {
        EncodingRequest good = BuildRequest("Good", Container.HlsTs);
        EncodingRequest bad = BuildRequest("Bad", Container.HlsTs);

        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "Good"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(MakePlan(OutputFormat.Hls, 1920, 1080, isHdrOutput: false));
        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "Bad"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((OutputPlan?)null);

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        Func<Task> act = async () => await orchestrator.DecomposeMergedAsync([good, bad], GroupTag);

        await act.Should().ThrowAsync<MergedEncodingIncompatibleException>();
    }

    [Fact]
    public async Task PlanMergedAsync_EmptyRequestList_ReturnsNull()
    {
        EncodingOrchestrator orchestrator = BuildOrchestrator();

        OutputPlan? result = await orchestrator.PlanMergedAsync([]);

        result.Should().BeNull();
    }
}
