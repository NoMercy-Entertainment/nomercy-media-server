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
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Tests.Encoder.Pipeline.Planners;

/// <summary>
/// A title whose only audio is Dolby (AC-3 / E-AC-3) or DTS must also get a
/// stereo AAC rendition, so a client that cannot decode Dolby still hears sound.
/// </summary>
public class AudioPlanBuilderAacFallbackTests
{
    private static AudioStreamInfo Audio(int index, string codec, string lang, int channels = 2) =>
        new(
            Index: index,
            Codec: codec,
            Channels: channels,
            SampleRate: 48000,
            BitRateKbps: 192,
            Language: lang,
            IsDefault: index == 1,
            IsForced: false
        );

    private static MediaInfo MediaWith(params AudioStreamInfo[] audio) =>
        new(
            FilePath: "/movie.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(24),
            OverallBitRateKbps: 4000,
            FileSizeBytes: 1,
            VideoStreams: [],
            AudioStreams: audio,
            SubtitleStreams: [],
            Chapters: []
        );

    private static AudioOutput Output(StreamPolicy policy, AudioCodecType codec) =>
        new(
            Policy: policy,
            Codec: codec,
            BitrateKbps: 192,
            Channels: 2,
            SampleRateHz: 48000,
            AllowedLanguages: AllowedLanguages.All,
            DefaultLanguage: null,
            Loudness: null,
            Downmix: null,
            SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
            PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
        );

    private static EncodingProfile ProfileWith(params AudioOutput[] audio) =>
        new(
            Id: Ulid.NewUlid(),
            Name: "aac-fallback",
            Container: Container.HlsFmp4,
            Video: null,
            Audio: audio,
            Subtitles: []
        );

    [Theory]
    [InlineData("eac3")]
    [InlineData("ac3")]
    [InlineData("dts")]
    public void DolbyOnlyCopy_AddsStereoAacRendition(string sourceCodec)
    {
        MediaInfo media = MediaWith(Audio(1, sourceCodec, "eng"));
        EncodingProfile profile = ProfileWith(Output(StreamPolicy.Copy, AudioCodecType.Copy));

        AudioOutputPlan[] plans = AudioPlanBuilder.Build(profile, media);

        plans.Should().HaveCount(2);
        plans[0].CodecToken.Should().Be(sourceCodec, "the original rendition stays first");
        plans[1].CodecToken.Should().Be("aac");
        plans[1].Channels.Should().Be(2);
        plans[1].Action.Should().Be(StreamAction.Transcode);
        plans[1].MapLabel.Should().Be("0:a:0");
        plans
            .Select(PlaylistGenerator.AudioVariantKey)
            .Distinct()
            .Should()
            .HaveCount(2, "the AAC rendition needs its own playlist path");
    }

    [Fact]
    public void AacOutputAlreadyPlanned_AddsNothing()
    {
        MediaInfo media = MediaWith(Audio(1, "eac3", "eng"));
        EncodingProfile profile = ProfileWith(
            Output(StreamPolicy.Transcode, AudioCodecType.Aac),
            Output(StreamPolicy.Copy, AudioCodecType.Copy)
        );

        AudioOutputPlan[] plans = AudioPlanBuilder.Build(profile, media);

        plans.Should().HaveCount(2);
        plans.Count(p => p.CodecToken == "aac").Should().Be(1);
    }

    [Fact]
    public void AacSource_AddsNothing()
    {
        MediaInfo media = MediaWith(Audio(1, "aac", "eng"));
        EncodingProfile profile = ProfileWith(Output(StreamPolicy.Copy, AudioCodecType.Copy));

        AudioOutputPlan[] plans = AudioPlanBuilder.Build(profile, media);

        plans.Should().ContainSingle();
    }

    [Fact]
    public void OnlyDolbyStreamsGetAnAacTwin()
    {
        MediaInfo media = MediaWith(Audio(1, "eac3", "eng"), Audio(2, "aac", "jpn"));
        EncodingProfile profile = ProfileWith(Output(StreamPolicy.Copy, AudioCodecType.Copy));

        AudioOutputPlan[] plans = AudioPlanBuilder.Build(profile, media);

        plans.Should().HaveCount(3);
        AudioOutputPlan twin = plans.Single(p => p.Action == StreamAction.Transcode);
        twin.Language.Should().Be("eng");
        twin.SourceStreamIndex.Should().Be(1);
    }
}
