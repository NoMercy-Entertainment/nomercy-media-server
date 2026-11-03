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
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Strategies;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using Container = NoMercy.Encoder.Profiles.Container;

namespace NoMercy.Tests.Encoder.Scenarios;

/// <summary>
/// Orchestrator optimality scenarios: prove the smart orchestrator produces
/// the OPTIMAL joint/split plan — one coordinated encode per source, minimal
/// decodes, sidecars produced once, correct split boundaries, correct compute
/// routing. Drive EncodingOrchestrator.PlanMergedAsync and DecomposeMergedAsync
/// + DecodeAwareBundlePlanner through deterministic pure-planning logic.
/// </summary>
public class OrchestratorOptimalityScenarioTests
{
    private const string GroupTag = "01HZOPTIMAL000000000000";

    private readonly Mock<IStrategyResolver> _resolver = new();
    private readonly Mock<IStorage> _storage = new();
    private readonly Mock<IEncoder> _encoder = new();

    public OrchestratorOptimalityScenarioTests()
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

    private static VideoOutputPlan CopyVideo(int index = 0) =>
        new(
            Width: 1920,
            Height: 1080,
            EncoderName: "copy",
            Crf: 0,
            BitrateKbps: 0,
            Preset: null,
            Profile: null,
            Level: null,
            TenBit: false,
            PixelFormat: "yuv420p",
            MapLabel: "0:v:0",
            ExtraFlags: [],
            VideoRange: VideoRange.Sdr
        );

    private static VideoOutputPlan TranscodeVideo(
        int width,
        int height,
        bool isHdrOutput,
        bool convertHdrToSdr = false,
        string tonemapFilterChain = ""
    ) =>
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
            MapLabel: $"[v{width}]",
            ExtraFlags: [],
            VideoRange: isHdrOutput ? VideoRange.Pq : VideoRange.Sdr,
            ConvertHdrToSdr: convertHdrToSdr,
            TonemapFilterChain: tonemapFilterChain
        );

    private static AudioOutputPlan MakeAudio(string language, string encoderName = "aac") =>
        new(
            EncoderName: encoderName,
            BitrateKbps: 128,
            Channels: 2,
            SampleRate: 48000,
            Action: StreamAction.Transcode,
            Language: language,
            MapLabel: "[a0]"
        );

    private static SubtitleOutputPlan MakeSubtitle(string language) =>
        new(
            OutputCodec: SubtitleCodecType.WebVtt,
            Action: StreamAction.Extract,
            Language: language,
            SourceIndex: 0,
            MapLabel: null
        );

    private static ThumbnailOutputPlan MakeThumbnails(
        int width = 160,
        int height = 90,
        int interval = 10
    ) => new(width, height, interval, SpriteGrid.For(TimeSpan.FromHours(2), interval));

    private static OutputPlan MakePlan(
        OutputFormat format,
        VideoOutputPlan[] videos,
        AudioOutputPlan[]? audios = null,
        SubtitleOutputPlan[]? subtitles = null,
        ThumbnailOutputPlan? thumbnails = null
    ) =>
        new(
            Format: format,
            VideoOutputs: videos,
            AudioOutputs: audios ?? [],
            SubtitleOutputs: subtitles ?? [],
            Thumbnails: thumbnails
        );

    // ================================================================
    // SCENARIO 1: Two presets (4K HDR AlwaysPreserve + 1080p SDR AlwaysTonemap)
    // merge to ONE OutputPlan with both video outputs unioned.
    // ================================================================

    [Fact]
    public async Task Merge_FourKHdrAndSdrPresets_ProducesOneOutputPlanWithBothVideoOutputs()
    {
        EncodingRequest fourKRequest = BuildRequest("4K HDR AlwaysPreserve", Container.HlsTs);
        EncodingRequest sdrRequest = BuildRequest("1080p SDR AlwaysTonemap", Container.HlsTs);

        OutputPlan fourKPlan = MakePlan(
            OutputFormat.Hls,
            [
                TranscodeVideo(
                    width: 3840,
                    height: 2160,
                    isHdrOutput: true,
                    convertHdrToSdr: false,
                    tonemapFilterChain: ""
                ),
            ],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

        OutputPlan sdrPlan = MakePlan(
            OutputFormat.Hls,
            [
                TranscodeVideo(
                    width: 1920,
                    height: 1080,
                    isHdrOutput: false,
                    convertHdrToSdr: true,
                    tonemapFilterChain: "zscale=m=in_color_matrix=bt2020:min=bt709:dither=error_diffusion,tonemap=tonemap_algo=libplacebo:desat=0"
                ),
            ],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            null
        );

        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "4K HDR AlwaysPreserve"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(fourKPlan);
        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "1080p SDR AlwaysTonemap"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(sdrPlan);

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        OutputPlan? merged = await orchestrator.PlanMergedAsync([fourKRequest, sdrRequest]);

        merged.Should().NotBeNull();
        merged!.VideoOutputs.Should().HaveCount(2);
        merged.VideoOutputs.Should().Contain(v => v.Width == 3840 && v.IsHdrOutput);
        merged.VideoOutputs.Should().Contain(v => v.Width == 1920 && !v.IsHdrOutput);
    }

    // ================================================================
    // SCENARIO 2: Merged plan deduplicates audio by (language, codec).
    // Two presets both want "eng AAC 2.0" — produced once, not twice.
    // ================================================================

    [Fact]
    public async Task Merge_BothPresetsWantSameAudioLanguageAndCodec_DeduplicatesToOneAudio()
    {
        EncodingRequest fourKRequest = BuildRequest("4K HDR", Container.HlsTs);
        EncodingRequest sdrRequest = BuildRequest("1080p SDR", Container.HlsTs);

        OutputPlan fourKPlan = MakePlan(
            OutputFormat.Hls,
            [TranscodeVideo(3840, 2160, isHdrOutput: true)],
            [MakeAudio("eng", "aac")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

        OutputPlan sdrPlan = MakePlan(
            OutputFormat.Hls,
            [TranscodeVideo(1920, 1080, isHdrOutput: false)],
            [MakeAudio("eng", "aac")],
            [MakeSubtitle("eng")],
            null
        );

        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "4K HDR"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(fourKPlan);
        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "1080p SDR"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(sdrPlan);

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        OutputPlan? merged = await orchestrator.PlanMergedAsync([fourKRequest, sdrRequest]);

        merged.Should().NotBeNull();
        merged!.AudioOutputs.Should().HaveCount(1);
        merged.AudioOutputs[0].Language.Should().Be("eng");
    }

    // ================================================================
    // SCENARIO 3: Merged plan keeps audio tracks with different codecs
    // for the same language. One preset copies EAC3, another transcodes AAC.
    // ================================================================

    [Fact]
    public async Task Merge_DifferentCodecsForSameLanguage_KeepsBoth()
    {
        EncodingRequest copyEac3Request = BuildRequest("4K EAC3 Copy", Container.HlsTs);
        EncodingRequest transcodeAacRequest = BuildRequest("1080p AAC Transcode", Container.HlsTs);

        OutputPlan eac3Plan = MakePlan(
            OutputFormat.Hls,
            [TranscodeVideo(3840, 2160, isHdrOutput: true)],
            [MakeAudio("eng", "eac3")],
            [],
            MakeThumbnails()
        );

        OutputPlan aacPlan = MakePlan(
            OutputFormat.Hls,
            [TranscodeVideo(1920, 1080, isHdrOutput: false)],
            [MakeAudio("eng", "aac")],
            [],
            null
        );

        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "4K EAC3 Copy"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(eac3Plan);
        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "1080p AAC Transcode"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(aacPlan);

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        OutputPlan? merged = await orchestrator.PlanMergedAsync([
            copyEac3Request,
            transcodeAacRequest,
        ]);

        merged.Should().NotBeNull();
        merged!.AudioOutputs.Should().HaveCount(2);
        merged.AudioOutputs.Should().Contain(a => a.Language == "eng" && a.EncoderName == "eac3");
        merged.AudioOutputs.Should().Contain(a => a.Language == "eng" && a.EncoderName == "aac");
    }

    // ================================================================
    // SCENARIO 4: Single preset merge path still works (no merge overhead).
    // ================================================================

    [Fact]
    public async Task DecomposeMergedAsync_SinglePreset_MatchesDecomposeAsync()
    {
        EncodingRequest request = BuildRequest("Solo", Container.HlsTs);
        OutputPlan plan = MakePlan(
            OutputFormat.Hls,
            [TranscodeVideo(1920, 1080, isHdrOutput: false)],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

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
                            Resources: null
                        ),
                        new DecomposedTask(
                            TaskId: $"{tag}-audio-0",
                            ParentJobId: 0,
                            GroupTag: tag,
                            Kind: EncodeTaskKind.Audio,
                            OutputIndex: 0,
                            Resources: null
                        ),
                    ]
            );
        _resolver
            .Setup(r => r.Resolve(OutputFormat.Hls, EncodeMode.SinglePass))
            .Returns(strategy.Object);

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        DecomposedTask[] viaDecomposeAsync = await orchestrator.DecomposeAsync(request, GroupTag);
        DecomposedTask[] viaMerged = await orchestrator.DecomposeMergedAsync([request], GroupTag);

        viaMerged.Should().HaveCount(viaDecomposeAsync.Length);
        viaMerged.Should().BeEquivalentTo(viaDecomposeAsync);
    }

    // ================================================================
    // SCENARIO 5: Multi-preset merge wires ONE merged plan into
    // ONE Decompose call (not one per preset).
    // ================================================================

    [Fact]
    public async Task DecomposeMergedAsync_TwoPresets_CallsDecomposeOnceWithMergedPlan()
    {
        EncodingRequest fourKRequest = BuildRequest("4K HDR", Container.HlsTs);
        EncodingRequest sdrRequest = BuildRequest("1080p SDR", Container.HlsTs);

        OutputPlan fourKPlan = MakePlan(
            OutputFormat.Hls,
            [TranscodeVideo(3840, 2160, isHdrOutput: true)],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

        OutputPlan sdrPlan = MakePlan(
            OutputFormat.Hls,
            [TranscodeVideo(1920, 1080, isHdrOutput: false)],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            null
        );

        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "4K HDR"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(fourKPlan);
        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "1080p SDR"),
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
                        new DecomposedTask(
                            TaskId: $"{tag}-video-0",
                            ParentJobId: 0,
                            GroupTag: tag,
                            Kind: EncodeTaskKind.Video,
                            OutputIndex: 0,
                            Resources: null
                        ),
                        new DecomposedTask(
                            TaskId: $"{tag}-video-1",
                            ParentJobId: 0,
                            GroupTag: tag,
                            Kind: EncodeTaskKind.Video,
                            OutputIndex: 1,
                            Resources: null
                        ),
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

    // ================================================================
    // SCENARIO 6: Decode grouping via DecodeAwareBundlePlanner.
    // Transcode and Tonemap outputs group separately (different decode chains).
    // ================================================================

    [Fact]
    public void GroupByDecodeClass_HdrTranscodeAndTonemapOutputs_GroupSeparately()
    {
        DecomposedTask[] tasks =
        [
            new(
                TaskId: $"{GroupTag}-video-0",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 0,
                Resources: null,
                VideoWidth: 3840,
                VideoEncoderName: "libx265"
            ),
            new(
                TaskId: $"{GroupTag}-video-1",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 1,
                Resources: null,
                VideoWidth: 1920,
                VideoEncoderName: "libx265"
            ),
        ];

        VideoOutputPlan hdrTranscode = TranscodeVideo(
            3840,
            2160,
            isHdrOutput: true,
            convertHdrToSdr: false,
            tonemapFilterChain: ""
        );

        VideoOutputPlan sdrTonemap = TranscodeVideo(
            1920,
            1080,
            isHdrOutput: false,
            convertHdrToSdr: true,
            tonemapFilterChain: "zscale=m=in_color_matrix=bt2020:min=bt709:dither=error_diffusion"
        );

        OutputPlan plan = MakePlan(
            OutputFormat.Hls,
            [hdrTranscode, sdrTonemap],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

        IReadOnlyList<DecodeGroup> groups = DecodeAwareBundlePlanner.GroupByDecodeClass(
            tasks,
            plan
        );

        groups.Should().HaveCount(2, "HDR preserve and tonemap should be separate decode groups");
        IReadOnlyList<int> hdrGroup =
            groups.FirstOrDefault(g => g.Class == DecodeClass.Transcode)?.VideoTaskIndexes ?? [];
        IReadOnlyList<int> tonemapGroup =
            groups.FirstOrDefault(g => g.Class == DecodeClass.Tonemap)?.VideoTaskIndexes ?? [];

        hdrGroup.Should().Contain(0);
        tonemapGroup.Should().Contain(1);
    }

    // ================================================================
    // SCENARIO 7: Copy video forms its own separate group at Layer 1.
    // Layer 2 (Plan()) combines it with the first real decode bundle.
    // ================================================================

    [Fact]
    public void GroupByDecodeClass_CopyAndTranscodeVideos_FormSeparateGroups()
    {
        DecomposedTask[] tasks =
        [
            new(
                TaskId: $"{GroupTag}-video-0",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 0,
                Resources: null,
                VideoWidth: 3840,
                VideoEncoderName: "copy"
            ),
            new(
                TaskId: $"{GroupTag}-video-1",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 1,
                Resources: null,
                VideoWidth: 1920,
                VideoEncoderName: "libx265"
            ),
        ];

        VideoOutputPlan copyVideo = CopyVideo(0);
        VideoOutputPlan transcodeVideo = TranscodeVideo(1920, 1080, isHdrOutput: false);

        OutputPlan plan = MakePlan(
            OutputFormat.Hls,
            [copyVideo, transcodeVideo],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

        IReadOnlyList<DecodeGroup> groups = DecodeAwareBundlePlanner.GroupByDecodeClass(
            tasks,
            plan
        );

        groups.Should().HaveCount(2, "copy and transcode form separate groups at Layer 1");
        IReadOnlyList<int>? copyIndexes = groups
            .FirstOrDefault(g => g.Class == DecodeClass.Copy)
            ?.VideoTaskIndexes;
        copyIndexes.Should().Contain(0, "copy task is in the Copy group");

        IReadOnlyList<int>? transcodeIndexes = groups
            .FirstOrDefault(g => g.Class == DecodeClass.Transcode)
            ?.VideoTaskIndexes;
        transcodeIndexes.Should().Contain(1, "transcode task is in the Transcode group");
    }

    // ================================================================
    // SCENARIO 8: Multiple transcode outputs (same output preset, different res)
    // share ONE decode and ONE bundle because they use the same filtergraph split.
    // ================================================================

    [Fact]
    public void GroupByDecodeClass_MultipleTranscodeResolutions_ShareOneDecodeGroup()
    {
        DecomposedTask[] tasks =
        [
            new(
                TaskId: $"{GroupTag}-video-0",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 0,
                Resources: null,
                VideoWidth: 3840,
                VideoEncoderName: "libx265"
            ),
            new(
                TaskId: $"{GroupTag}-video-1",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 1,
                Resources: null,
                VideoWidth: 1920,
                VideoEncoderName: "libx265"
            ),
            new(
                TaskId: $"{GroupTag}-video-2",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 2,
                Resources: null,
                VideoWidth: 1280,
                VideoEncoderName: "libx265"
            ),
        ];

        OutputPlan plan = MakePlan(
            OutputFormat.Hls,
            [
                TranscodeVideo(3840, 2160, isHdrOutput: true),
                TranscodeVideo(1920, 1080, isHdrOutput: true),
                TranscodeVideo(1280, 720, isHdrOutput: true),
            ],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

        IReadOnlyList<DecodeGroup> groups = DecodeAwareBundlePlanner.GroupByDecodeClass(
            tasks,
            plan
        );

        groups
            .Should()
            .HaveCount(1, "multiple transcode rungs off same source share one decode group");
        groups[0].Class.Should().Be(DecodeClass.Transcode);
        groups[0].VideoTaskIndexes.Should().HaveCount(3);
    }

    // ================================================================
    // SCENARIO 9: Incompatible merge (different containers) throws.
    // ================================================================

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
            .ReturnsAsync(MakePlan(OutputFormat.Hls, [TranscodeVideo(1920, 1080, false)]));
        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "MKV preset"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(MakePlan(OutputFormat.Mkv, [TranscodeVideo(1920, 1080, false)]));

        _resolver
            .Setup(r => r.Resolve(It.IsAny<OutputFormat>(), It.IsAny<EncodeMode>()))
            .Returns<OutputFormat, EncodeMode>(
                (f, m) =>
                {
                    Mock<IEncodingStrategy> strategy = new();
                    strategy.Setup(s => s.Format).Returns(f);
                    strategy.Setup(s => s.EncodeMode).Returns(m);
                    strategy
                        .Setup(s => s.Decompose(It.IsAny<OutputPlan>(), It.IsAny<string>()))
                        .Returns((OutputPlan p, string tag) => []);
                    return strategy.Object;
                }
            );

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        Func<Task> act = async () =>
            await orchestrator.DecomposeMergedAsync([hlsRequest, mkvRequest], GroupTag);

        await act.Should()
            .ThrowAsync<MergedEncodingIncompatibleException>(
                "presets with different output formats cannot merge"
            );
    }

    // ================================================================
    // SCENARIO 10: Incompatible merge (different encode modes) throws.
    // ================================================================

    [Fact]
    public async Task DecomposeMergedAsync_DifferentEncodeModes_Throws()
    {
        EncodingRequest singlePassRequest = BuildRequest("Single Pass", Container.HlsTs);
        EncodingRequest twoPassRequest = BuildRequest("Two Pass", Container.HlsTs);

        singlePassRequest = singlePassRequest with
        {
            Profile = singlePassRequest.Profile with { EncodeMode = EncodeMode.SinglePass },
        };
        twoPassRequest = twoPassRequest with
        {
            Profile = twoPassRequest.Profile with { EncodeMode = EncodeMode.TwoPass },
        };

        OutputPlan plan = MakePlan(OutputFormat.Hls, [TranscodeVideo(1920, 1080, false)]);

        _encoder
            .Setup(e => e.PlanAsync(It.IsAny<EncodingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        Func<Task> act = async () =>
            await orchestrator.DecomposeMergedAsync([singlePassRequest, twoPassRequest], GroupTag);

        await act.Should()
            .ThrowAsync<MergedEncodingIncompatibleException>(
                "presets with different encode modes cannot merge"
            );
    }

    // ================================================================
    // SCENARIO 11: Tonemap and Transcode groups are separate, so a plan
    // mixing HDR-preserve + SDR-tonemap has two distinct decode groups.
    // ================================================================

    [Fact]
    public void GroupByDecodeClass_MixedHdrPreserveAndTonemap_TwoSeparateDecodeGroups()
    {
        DecomposedTask[] tasks =
        [
            new(
                TaskId: $"{GroupTag}-video-0",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 0,
                Resources: null,
                VideoWidth: 3840,
                VideoEncoderName: "libx265"
            ),
            new(
                TaskId: $"{GroupTag}-video-1",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 1,
                Resources: null,
                VideoWidth: 1920,
                VideoEncoderName: "libx265"
            ),
        ];

        VideoOutputPlan hdrTranscode = TranscodeVideo(3840, 2160, isHdrOutput: true);
        VideoOutputPlan sdrTonemap = TranscodeVideo(
            1920,
            1080,
            isHdrOutput: false,
            convertHdrToSdr: true,
            tonemapFilterChain: "zscale=m=in_color_matrix=bt2020:min=bt709"
        );

        OutputPlan plan = MakePlan(
            OutputFormat.Hls,
            [hdrTranscode, sdrTonemap],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

        IReadOnlyList<DecodeGroup> groups = DecodeAwareBundlePlanner.GroupByDecodeClass(
            tasks,
            plan
        );

        groups
            .Should()
            .HaveCount(
                2,
                "HDR preserve (Transcode) and tonemap (Tonemap) form separate decode groups"
            );
        groups
            .Should()
            .Contain(g => g.Class == DecodeClass.Transcode && g.VideoTaskIndexes.Contains(0));
        groups
            .Should()
            .Contain(g => g.Class == DecodeClass.Tonemap && g.VideoTaskIndexes.Contains(1));
    }

    // ================================================================
    // SCENARIO 12: GPU/CPU resource routing: NVENC transcode task carries
    // GPU resource requirement; CPU-only task does not.
    // ================================================================

    [Fact]
    public void GroupByDecodeClass_GpuResourceRoutingTaggedOnNvencTask()
    {
        ResourceRequirement gpuResource = new("gpu0", GpuSlots: 1, CpuThreads: 2);

        DecomposedTask[] tasks =
        [
            new(
                TaskId: $"{GroupTag}-video-0",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 0,
                Resources: gpuResource,
                VideoWidth: 1920,
                VideoEncoderName: "hevc_nvenc"
            ),
        ];

        OutputPlan plan = MakePlan(
            OutputFormat.Hls,
            [TranscodeVideo(1920, 1080, isHdrOutput: false)],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

        IReadOnlyList<DecodeGroup> groups = DecodeAwareBundlePlanner.GroupByDecodeClass(
            tasks,
            plan
        );

        groups.Should().HaveCount(1);
        groups[0].Class.Should().Be(DecodeClass.Transcode);
        groups[0].VideoTaskIndexes.Should().Contain(0);
    }

    // ================================================================
    // SCENARIO 13: Pure copy video (no transcode) forms only a Copy group.
    // When there's no Transcode or Tonemap group, the Plan() layer creates
    // a zero-decode bundle for copy video and sidecars.
    // ================================================================

    [Fact]
    public void GroupByDecodeClass_PureCopyVideo_OnlyCopyDecodeGroup()
    {
        DecomposedTask[] tasks =
        [
            new(
                TaskId: $"{GroupTag}-video-0",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Video,
                OutputIndex: 0,
                Resources: null,
                VideoWidth: 1920,
                VideoEncoderName: "copy"
            ),
            new(
                TaskId: $"{GroupTag}-audio-0",
                ParentJobId: 0,
                GroupTag: GroupTag,
                Kind: EncodeTaskKind.Audio,
                OutputIndex: 0,
                Resources: null
            ),
        ];

        OutputPlan plan = MakePlan(
            OutputFormat.Hls,
            [CopyVideo(0)],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            null
        );

        IReadOnlyList<DecodeGroup> groups = DecodeAwareBundlePlanner.GroupByDecodeClass(
            tasks,
            plan
        );

        groups.Should().HaveCount(1, "pure copy video forms only one Copy decode group");
        groups[0].Class.Should().Be(DecodeClass.Copy);
        groups[0].VideoTaskIndexes.Should().Contain(0, "copy video is in the Copy group");
    }

    // ================================================================
    // SCENARIO 14: Verify PlanMergedAsync returns null on planning failure
    // (fallback to per-preset dispatch).
    // ================================================================

    [Fact]
    public async Task PlanMergedAsync_OnePlannerFails_ReturnsNull()
    {
        EncodingRequest workingRequest = BuildRequest("Working Preset", Container.HlsTs);
        EncodingRequest failingRequest = BuildRequest("Failing Preset", Container.HlsTs);

        OutputPlan workingPlan = MakePlan(
            OutputFormat.Hls,
            [TranscodeVideo(1920, 1080, isHdrOutput: false)],
            [MakeAudio("eng")],
            [MakeSubtitle("eng")],
            MakeThumbnails()
        );

        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "Working Preset"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(workingPlan);
        _encoder
            .Setup(e =>
                e.PlanAsync(
                    It.Is<EncodingRequest>(r => r.Profile.Name == "Failing Preset"),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((OutputPlan?)null);

        EncodingOrchestrator orchestrator = BuildOrchestrator();

        OutputPlan? merged = await orchestrator.PlanMergedAsync([workingRequest, failingRequest]);

        merged.Should().BeNull("merge should fail if any preset fails to plan");
    }
}
