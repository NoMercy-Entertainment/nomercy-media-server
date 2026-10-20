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

using FluentAssertions;
using NoMercy.Encoder.Analysis;

namespace NoMercy.Tests.Encoder.Analysis;

[Trait("Category", "Unit")]
public class MediaAnalyzerAudioTrackTests
{
    private const string Payload = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
              "pix_fmt": "yuv420p", "r_frame_rate": "24/1" },
            { "index": 1, "codec_type": "audio", "codec_name": "eac3", "profile": "Dolby Digital Plus + Dolby Atmos",
              "channels": 2, "channel_layout": "stereo", "sample_rate": "48000", "bit_rate": "224000",
              "disposition": { "default": 1, "forced": 0 }, "tags": { "language": "jpn" } },
            { "index": 2, "codec_type": "audio", "codec_name": "aac", "profile": "LC",
              "channels": 6, "channel_layout": "5.1", "sample_rate": "44100", "bit_rate": "384000",
              "disposition": { "default": 0, "forced": 0 }, "tags": { "language": "eng" } }
          ],
          "format": { "format_name": "matroska,webm", "duration": "60.0", "size": "1000", "bit_rate": "8000" }
        }
        """;

    [Fact]
    public void ParseFfprobeJson_AudioStreams_KeepProfileAndChannelLayout()
    {
        MediaInfo info = MediaAnalyzer.ParseFfprobeJson(Payload, "/media/example.mkv");

        info.AudioStreams.Should().HaveCount(2);

        AudioStreamInfo stereo = info.AudioStreams[0];
        stereo.Codec.Should().Be("eac3");
        stereo.Profile.Should().Be("Dolby Digital Plus + Dolby Atmos");
        stereo.Channels.Should().Be(2);
        stereo.ChannelLayout.Should().Be("stereo");

        AudioStreamInfo surround = info.AudioStreams[1];
        surround.Codec.Should().Be("aac");
        surround.Profile.Should().Be("LC");
        surround.Channels.Should().Be(6);
        surround.ChannelLayout.Should().Be("5.1");
    }
}
