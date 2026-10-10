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

using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Pipeline.Optimizer;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;
using V2RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;

namespace NoMercy.Tests.Encoder.Pipeline.Optimizer;

public class ExecutionGraphBuilderThumbnailTests
{
    [Fact]
    public void Thumbnail_HdrSource_DependsOnTonemapNode()
    {
        MediaInfo media = BuildMedia(transfer: "smpte2084", width: 3840, height: 2160);
        EncodingProfile profile = BuildProfileWithThumbnails();

        List<ExecutionNode> nodes = new ExecutionGraphBuilder().BuildGraph(
            media,
            profile,
            ResolveSingle()
        );

        ExecutionNode tonemap = nodes.Single(n => n.Operation == OperationType.Tonemap);
        ExecutionNode thumb = nodes.Single(n => n.Operation == OperationType.ThumbnailCapture);

        thumb
            .DependsOn.Should()
            .Contain(tonemap.Id, "HDR sprites derive from the SDR intermediate");
    }

    [Fact]
    public void Thumbnail_SdrSource_HasNoVideoDependency()
    {
        MediaInfo media = BuildMedia(transfer: "bt709", width: 1920, height: 1080);
        EncodingProfile profile = BuildProfileWithThumbnails();

        List<ExecutionNode> nodes = new ExecutionGraphBuilder().BuildGraph(
            media,
            profile,
            ResolveSingle()
        );

        nodes.Should().NotContain(n => n.Operation == OperationType.Tonemap);
        ExecutionNode thumb = nodes.Single(n => n.Operation == OperationType.ThumbnailCapture);
        thumb.DependsOn.Should().BeEmpty("SDR sprites read the decoded source directly");
    }

    private static MediaInfo BuildMedia(string transfer, int width, int height) =>
        new(
            FilePath: "/media/test.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(90),
            OverallBitRateKbps: 50000,
            FileSizeBytes: 30_000_000_000,
            VideoStreams:
            [
                new(
                    Index: 0,
                    Codec: "hevc",
                    Width: width,
                    Height: height,
                    FrameRate: 24.0,
                    BitDepth: transfer == "smpte2084" ? 10 : 8,
                    PixelFormat: transfer == "smpte2084" ? "yuv420p10le" : "yuv420p",
                    ColorPrimaries: transfer == "smpte2084" ? "bt2020" : "bt709",
                    ColorTransfer: transfer,
                    ColorSpace: transfer == "smpte2084" ? "bt2020nc" : "bt709",
                    IsDefault: true,
                    BitRateKbps: 45000
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

    private static EncodingProfile BuildProfileWithThumbnails() =>
        new(
            Id: Ulid.NewUlid(),
            Name: "Thumb Test",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H265,
                Width: 1920,
                Height: 1080,
                RateControl: V2RateControlMode.Crf,
                Crf: 22,
                BitrateKbps: 5000,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "medium",
                CodecProfile: CodecProfile.Main,
                Level: "5.1",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: true,
                SegmentNameTemplate: "video/{label}",
                PlaylistNameTemplate: "video/{label}/playlist"
            ),
            Audio: [],
            Subtitles: [],
            Thumbnails: new(320, 10)
        );

    private static ResolvedCodec[] ResolveSingle() =>
        [
            new(
                FfmpegEncoderName: "libx265",
                EncoderInfo: new(
                    FfmpegName: "libx265",
                    RequiredVendor: null,
                    Presets: ["medium"],
                    Profiles: ["main"],
                    Levels: ["5.1"],
                    QualityRange: new(0, 51, 28),
                    SupportedRateControl: [RateControlMode.Crf],
                    Supports10Bit: true,
                    SupportsHdr: true,
                    MaxConcurrentSessions: int.MaxValue,
                    PixelFormat10Bit: "yuv420p10le",
                    VendorSpecificFlags: new()
                ),
                Device: null,
                DefaultRateControl: RateControlMode.Crf
            ),
        ];
}
