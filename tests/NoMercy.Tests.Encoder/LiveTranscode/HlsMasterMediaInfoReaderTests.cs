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
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Tests.Encoder.LiveTranscode;

public class HlsMasterMediaInfoReaderTests
{
    private static string Master(string groupId, string mediaExtra = "") =>
        "#EXTM3U\n"
        + $"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"{groupId}\",LANGUAGE=\"eng\",URI=\"a/playlist.m3u8\"{mediaExtra}\n"
        + $"#EXT-X-STREAM-INF:BANDWIDTH=4000000,CODECS=\"avc1.640028\",RESOLUTION=1920x1080,VIDEO-RANGE=SDR,AUDIO=\"{groupId}\"\n"
        + "v/playlist.m3u8\n";

    [Theory]
    [InlineData("audio_eac3", "eac3")]
    [InlineData("audio_ac3", "ac3")]
    [InlineData("audio_aac", "aac")]
    public void TryRead_VideoOnlyCodecs_TakesAudioCodecFromTheAudioGroup(
        string groupId,
        string expected
    )
    {
        IReadOnlyList<MediaInfo>? result = HlsMasterMediaInfoReader.TryRead(
            Master(groupId),
            "master.m3u8"
        );

        result.Should().NotBeNull();
        result![0].AudioStreams.Should().ContainSingle().Which.Codec.Should().Be(expected);
    }

    [Fact]
    public void TryRead_AudioGroupWithUnknownCodec_ReturnsNullSoTheFileIsProbed()
    {
        IReadOnlyList<MediaInfo>? result = HlsMasterMediaInfoReader.TryRead(
            Master("audio_mystery"),
            "master.m3u8"
        );

        result.Should().BeNull();
    }
}
