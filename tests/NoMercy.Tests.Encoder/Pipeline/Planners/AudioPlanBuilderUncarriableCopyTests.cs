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
/// A preset that says "keep the source audio" must not copy a codec the HLS/MP4
/// muxer refuses (TrueHD): ffmpeg stops with "truehd in MP4 support is
/// experimental" and the whole encode fails.
/// </summary>
public class AudioPlanBuilderUncarriableCopyTests
{
    private static AudioStreamInfo Audio(string codec, int channels) =>
        new(
            Index: 1,
            Codec: codec,
            Channels: channels,
            SampleRate: 48000,
            BitRateKbps: 4000,
            Language: "eng",
            IsDefault: true,
            IsForced: false
        );

    private static MediaInfo MediaWith(AudioStreamInfo audio) =>
        new(
            FilePath: "/movie.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromMinutes(24),
            OverallBitRateKbps: 4000,
            FileSizeBytes: 1,
            VideoStreams: [],
            AudioStreams: [audio],
            SubtitleStreams: [],
            Chapters: []
        );

    private static EncodingProfile CopyProfile() =>
        new(
            Id: Ulid.NewUlid(),
            Name: "copy-audio",
            Container: Container.HlsFmp4,
            Video: null,
            Audio:
            [
                new(
                    Policy: StreamPolicy.Copy,
                    Codec: AudioCodecType.Copy,
                    BitrateKbps: 0,
                    Channels: 0,
                    SampleRateHz: 0,
                    AllowedLanguages: AllowedLanguages.All,
                    DefaultLanguage: null,
                    Loudness: null,
                    Downmix: null,
                    SegmentNameTemplate: "audio_{lang}_{codec}/audio_{lang}_{codec}",
                    PlaylistNameTemplate: "audio_{lang}_{codec}/audio_{lang}_{codec}"
                ),
            ],
            Subtitles: []
        );

    [Fact]
    public void TrueHdSevenOne_UnderCopyPolicy_IsTranscodedToSixChannelEac3()
    {
        AudioOutputPlan[] plans = AudioPlanBuilder.Build(CopyProfile(), MediaWith(Audio("truehd", 8)));

        plans[0].Action.Should().Be(StreamAction.Transcode);
        plans[0].CodecToken.Should().Be("eac3");
        plans[0].Channels.Should().Be(6);
        plans[0].BitrateKbps.Should().Be(640);
    }

    [Fact]
    public void TrueHdStereo_UnderCopyPolicy_IsTranscodedToAac()
    {
        AudioOutputPlan[] plans = AudioPlanBuilder.Build(CopyProfile(), MediaWith(Audio("truehd", 2)));

        plans.Should().ContainSingle();
        plans[0].Action.Should().Be(StreamAction.Transcode);
        plans[0].CodecToken.Should().Be("aac");
    }

    [Fact]
    public void Eac3_UnderCopyPolicy_StaysACopy()
    {
        AudioOutputPlan[] plans = AudioPlanBuilder.Build(CopyProfile(), MediaWith(Audio("eac3", 6)));

        plans[0].Action.Should().Be(StreamAction.Copy);
        plans[0].CodecToken.Should().Be("eac3");
    }
}
