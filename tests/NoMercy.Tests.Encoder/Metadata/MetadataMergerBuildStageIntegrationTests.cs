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
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Metadata;
using NoMercy.Encoder.Naming;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Optimizer;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.PostProcess;
using NoMercy.Tests.Encoder.Pipeline.Stages;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Metadata;

/// <summary>
/// Integration test: when a copy-mode preset is active, BuildStage passes
/// merged tracks (source language wins, DB title wins) to MetadataInjector.
/// </summary>
public class MetadataMergerBuildStageIntegrationTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static BuildStage CreateStage(IMetadataInjector injector, IMetadataMerger merger) =>
        new(
            new() { FfmpegPathOverride = "ffmpeg", FfprobePathOverride = "ffprobe" },
            new FontExtractor(TestStorageFactory.CreateLocal()),
            new SubtitleExtractor(),
            OutputStrategyFactoryTestHelper.Create(),
            [],
            NullLogger<BuildStage>.Instance,
            TestStorageFactory.CreateLocal(),
            metadataInjector: injector,
            metadataMerger: merger
        );

    /// <summary>
    /// Copy-mode MKV plan: video EncoderName = "copy", audio Action = Copy.
    /// </summary>
    private static ExecutionPlan BuildCopyMkvPlan() =>
        new(
            Groups:
            [
                new(
                    GroupId: "g0",
                    Nodes:
                    [
                        new("decode_0", OperationType.Decode, [], new()),
                        new("encode_0", OperationType.Encode, ["decode_0"], new()),
                    ],
                    DeviceId: null,
                    GpuSlotsRequired: 0,
                    CpuThreadsRequired: 2,
                    RequiresGpu: false,
                    Priority: 1
                ),
            ],
            EstimatedTotalDuration: TimeSpan.FromMinutes(90),
            OutputPlan: new(
                Format: OutputFormat.Mkv,
                VideoOutputs:
                [
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
                        ExtraFlags: new()
                    ),
                ],
                AudioOutputs:
                [
                    new(
                        EncoderName: "copy",
                        BitrateKbps: 0,
                        Channels: 0,
                        SampleRate: 0,
                        Action: StreamAction.Copy,
                        Language: "eng",
                        MapLabel: "0:a:0"
                    ),
                ],
                SubtitleOutputs: [],
                Thumbnails: null
            )
        );

    private static MediaInfo BuildMediaInfo() =>
        new(
            FilePath: "/movies/fight_club.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(139),
            OverallBitRateKbps: 20000,
            FileSizeBytes: 20_000_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "hevc",
                    Width: 1920,
                    Height: 1080,
                    FrameRate: 23.976,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: null,
                    ColorTransfer: null,
                    ColorSpace: null,
                    IsDefault: true,
                    BitRateKbps: 18000
                ),
            ],
            AudioStreams:
            [
                // Source says "jpn" — DB will supply title override but NOT language
                new(
                    Index: 0,
                    Codec: "truehd",
                    Channels: 8,
                    SampleRate: 48000,
                    BitRateKbps: 5000,
                    Language: "jpn",
                    IsDefault: true,
                    IsForced: false
                ),
            ],
            SubtitleStreams: [],
            Chapters: []
        );

    // -----------------------------------------------------------------------
    // Integration test
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BuildStage_CopyMode_MergedTracksReflectSourceLanguageAndDbTitle()
    {
        // Source stream at OutputIndex 0: language=jpn, no title
        // DB says: language=eng (ignored), title="Japanese TrueHD 7.1"
        // Expected merged: language=jpn (source wins), title="Japanese TrueHD 7.1" (DB wins)

        CapturingMetadataInjector injector = new();
        MetadataMerger merger = new();
        BuildStage stage = CreateStage(injector, merger);

        // DB track: language=eng (should be overridden by source), explicit title
        TrackMetadata dbTrack = new(
            OutputIndex: 0,
            Kind: "audio",
            Language: "eng",
            Title: "Japanese TrueHD 7.1",
            IsDefault: true,
            IsForced: false
        );

        // Source tracks injected via EncodingContext
        SourceTrackMetadata srcTrack = new(
            OutputIndex: 0,
            Kind: "audio",
            Language: "jpn",
            Title: null,
            Comment: null,
            IsDefault: true,
            IsForced: false
        );

        EncodingContext context = EncodingContext.Create() with
        {
            MediaItem = new MovieMediaRef(
                Type: MediaType.Movie,
                Id: 550,
                Title: "Fight Club",
                Year: 1999
            ),
            MediaInfo = BuildMediaInfo(),
            SourceTracks = [srcTrack],
            DbTracks = [dbTrack],
            EnableMetadataInjection = true,
        };

        ExecutionPlan plan = BuildCopyMkvPlan();
        BuildInput input = new(
            plan,
            "/movies/fight_club.mkv",
            "/tmp/nmtest-output/fc",
            "Fight Club.NoMercy"
        );

        StageResult result = await stage.ExecuteAsync(input, context, default);

        result.Should().BeOfType<StageSuccess<FfmpegCommand[]>>();

        // The capturing injector recorded the context it received
        injector.CapturedContext.Should().NotBeNull();
        IReadOnlyList<TrackMetadata> tracks = injector.CapturedContext!.Tracks;

        tracks.Should().ContainSingle();
        TrackMetadata merged = tracks[0];
        merged.Language.Should().Be("jpn", "source language must win over DB language");
        merged.Title.Should().Be("Japanese TrueHD 7.1", "DB title must win when non-empty");
        merged.IsDefault.Should().BeTrue("DB IsDefault wins");
    }

    // -----------------------------------------------------------------------
    // Helper: injector that captures context for assertion
    // -----------------------------------------------------------------------

    private sealed class CapturingMetadataInjector : IMetadataInjector
    {
        public MetadataInjectionContext? CapturedContext { get; private set; }

        public IReadOnlyList<string> BuildArgs(MetadataInjectionContext ctx)
        {
            CapturedContext = ctx;
            return [];
        }
    }
}
