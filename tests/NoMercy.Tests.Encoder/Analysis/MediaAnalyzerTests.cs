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

using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Analysis;

public class MediaAnalyzerTests
{
    [Fact]
    public void Parse_RotationFromDisplayMatrix_PreservesSignedAngle()
    {
        string json = """
            {
              "streams": [
                {
                  "index": 0,
                  "codec_type": "video",
                  "codec_name": "h264",
                  "width": 1920,
                  "height": 1080,
                  "side_data_list": [
                    { "side_data_type": "Display Matrix", "rotation": -90 }
                  ]
                }
              ],
              "format": { "duration": "60" }
            }
            """;

        MediaInfo media = MediaAnalyzer.ParseFfprobeJson(json, "/media/portrait.mp4");

        media.VideoStreams[0].Rotation.Should().Be(-90);
        media.PrimaryRotation.Should().Be(-90);
    }

    [Fact]
    public void Parse_RotationFromLegacyTag_WhenDisplayMatrixIsAbsent()
    {
        string json = """
            {
              "streams": [
                {
                  "index": 0,
                  "codec_type": "video",
                  "codec_name": "h264",
                  "width": 1920,
                  "height": 1080,
                  "tags": { "rotate": "90" }
                }
              ],
              "format": { "duration": "60" }
            }
            """;

        MediaInfo media = MediaAnalyzer.ParseFfprobeJson(json, "/media/portrait.mp4");

        media.VideoStreams[0].Rotation.Should().Be(90);
    }

    private static string LoadFixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Analysis", "Fixtures", name);
        return File.ReadAllText(path);
    }

    private static MediaInfo Parse(string fixtureName, string filePath = "/media/test.mkv")
    {
        string json = LoadFixture(fixtureName);
        return MediaAnalyzer.ParseFfprobeJson(json, filePath);
    }

    [Fact]
    public void Parse_1080p_H264_Aac_CorrectStreamCounts()
    {
        MediaInfo info = Parse("fixture-1080p-h264-aac.json");

        info.VideoStreams.Should().HaveCount(1);
        info.AudioStreams.Should().HaveCount(1);
        info.SubtitleStreams.Should().HaveCount(0);
        info.Chapters.Should().HaveCount(0);
        info.HasVideo.Should().BeTrue();
        info.HasAudio.Should().BeTrue();
        info.HasSubtitles.Should().BeFalse();
    }

    [Fact]
    public void Parse_1080p_H264_Aac_VideoFields()
    {
        MediaInfo info = Parse("fixture-1080p-h264-aac.json");
        VideoStreamInfo video = info.VideoStreams[0];

        video.Index.Should().Be(0);
        video.Codec.Should().Be("h264");
        video.Width.Should().Be(1920);
        video.Height.Should().Be(1080);
        video.BitDepth.Should().Be(8);
        video.PixelFormat.Should().Be("yuv420p");
        video.ColorPrimaries.Should().Be("bt709");
        video.ColorTransfer.Should().Be("bt709");
        video.ColorSpace.Should().Be("bt709");
        video.IsDefault.Should().BeTrue();
        video.IsHdr.Should().BeFalse();
        video.BitRateKbps.Should().Be(8000);
    }

    [Fact]
    public void Parse_1080p_H264_Aac_FrameRate()
    {
        MediaInfo info = Parse("fixture-1080p-h264-aac.json");
        VideoStreamInfo video = info.VideoStreams[0];

        video.FrameRate.Should().BeApproximately(23.976, 0.001);
    }

    [Fact]
    public void Parse_1080p_H264_Aac_FormatFields()
    {
        MediaInfo info = Parse("fixture-1080p-h264-aac.json");

        info.Format.Should().Be("matroska,webm");
        info.Duration.Should().Be(TimeSpan.FromSeconds(7200));
        info.OverallBitRateKbps.Should().Be(8192);
        info.FileSizeBytes.Should().Be(7_372_800_000L);
    }

    [Fact]
    public void Parse_1080p_H264_Aac_AudioFields()
    {
        MediaInfo info = Parse("fixture-1080p-h264-aac.json");
        AudioStreamInfo audio = info.AudioStreams[0];

        audio.Index.Should().Be(1);
        audio.Codec.Should().Be("aac");
        audio.Channels.Should().Be(2);
        audio.SampleRate.Should().Be(48000);
        audio.BitRateKbps.Should().Be(192);
        audio.Language.Should().Be("eng");
        audio.IsDefault.Should().BeTrue();
        audio.IsForced.Should().BeFalse();
    }

    [Fact]
    public void Parse_4k_Hdr_Hevc_IsHdr()
    {
        MediaInfo info = Parse("fixture-4k-hdr-hevc-truehd.json");
        VideoStreamInfo video = info.VideoStreams[0];

        video.Codec.Should().Be("hevc");
        video.Width.Should().Be(3840);
        video.Height.Should().Be(2160);
        video.BitDepth.Should().Be(10);
        video.ColorPrimaries.Should().Be("bt2020");
        video.ColorTransfer.Should().Be("smpte2084");
        video.IsHdr.Should().BeTrue();
    }

    [Fact]
    public void Parse_4k_Hdr_Hevc_ChaptersParsed()
    {
        MediaInfo info = Parse("fixture-4k-hdr-hevc-truehd.json");

        info.Chapters.Should().HaveCount(3);
        info.Chapters[0].Title.Should().Be("Chapter 1");
        info.Chapters[0].Start.Should().Be(TimeSpan.Zero);
        info.Chapters[0].End.Should().Be(TimeSpan.FromSeconds(1200));
        info.Chapters[1].Title.Should().Be("Chapter 2");
        info.Chapters[1].Start.Should().Be(TimeSpan.FromSeconds(1200));
        info.Chapters[2].Title.Should().Be("Chapter 3");
        info.Chapters[2].End.Should().Be(TimeSpan.FromSeconds(7200));
    }

    [Fact]
    public void Parse_4k_Hdr_Hevc_MultipleAudioStreams()
    {
        MediaInfo info = Parse("fixture-4k-hdr-hevc-truehd.json");

        info.AudioStreams.Should().HaveCount(2);
        info.AudioStreams[0].Codec.Should().Be("truehd");
        info.AudioStreams[0].Channels.Should().Be(8);
        info.AudioStreams[0].IsDefault.Should().BeTrue();
        info.AudioStreams[1].Codec.Should().Be("ac3");
        info.AudioStreams[1].Channels.Should().Be(6);
        info.AudioStreams[1].IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Parse_Dvd_Mpeg2_Ac3_CorrectCodecAndResolution()
    {
        MediaInfo info = Parse("fixture-dvd-mpeg2-ac3.json");
        VideoStreamInfo video = info.VideoStreams[0];

        video.Codec.Should().Be("mpeg2video");
        video.Width.Should().Be(720);
        video.Height.Should().Be(480);
        video.BitDepth.Should().Be(8);
        info.Format.Should().Be("mpeg");
    }

    [Fact]
    public void Parse_Dvd_Mpeg2_Ac3_BitmapSubtitle()
    {
        MediaInfo info = Parse("fixture-dvd-mpeg2-ac3.json");

        info.SubtitleStreams.Should().HaveCount(1);
        SubtitleStreamInfo sub = info.SubtitleStreams[0];
        sub.Codec.Should().Be("dvd_subtitle");
        sub.IsTextBased.Should().BeFalse();
        sub.Language.Should().Be("eng");
    }

    [Fact]
    public void Parse_MultiAudioSubs_CorrectCounts()
    {
        MediaInfo info = Parse("fixture-multi-audio-subs.json");

        info.VideoStreams.Should().HaveCount(1);
        info.AudioStreams.Should().HaveCount(3);
        info.SubtitleStreams.Should().HaveCount(5);
    }

    [Fact]
    public void Parse_MultiAudioSubs_ForcedSubtitleDetected()
    {
        MediaInfo info = Parse("fixture-multi-audio-subs.json");

        SubtitleStreamInfo forced = info.SubtitleStreams.Single(s => s.IsForced);
        forced.Index.Should().Be(6);
        forced.Codec.Should().Be("ass");
        forced.Language.Should().Be("eng");
    }

    [Fact]
    public void Parse_MultiAudioSubs_TextVsBitmapClassification()
    {
        MediaInfo info = Parse("fixture-multi-audio-subs.json");

        IReadOnlyList<SubtitleStreamInfo> subs = info.SubtitleStreams;

        subs[0].Codec.Should().Be("ass");
        subs[0].IsTextBased.Should().BeTrue();

        subs[1].Codec.Should().Be("ass");
        subs[1].IsTextBased.Should().BeTrue();

        subs[2].Codec.Should().Be("ass");
        subs[2].IsTextBased.Should().BeTrue();

        subs[3].Codec.Should().Be("hdmv_pgs_subtitle");
        subs[3].IsTextBased.Should().BeFalse();

        subs[4].Codec.Should().Be("subrip");
        subs[4].IsTextBased.Should().BeTrue();
    }

    [Fact]
    public void Parse_AudioCd_Flac_HasVideoFalse()
    {
        MediaInfo info = Parse("fixture-audio-cd-flac.json");

        info.HasVideo.Should().BeFalse();
        info.HasAudio.Should().BeTrue();
        info.VideoStreams.Should().HaveCount(0);
    }

    [Fact]
    public void Parse_AudioCd_Flac_AudioFields()
    {
        MediaInfo info = Parse("fixture-audio-cd-flac.json");
        AudioStreamInfo audio = info.AudioStreams[0];

        audio.Codec.Should().Be("flac");
        audio.Channels.Should().Be(2);
        audio.SampleRate.Should().Be(44100);
    }

    [Fact]
    public void Parse_AudioCd_Flac_ChaptersParsed()
    {
        MediaInfo info = Parse("fixture-audio-cd-flac.json");

        info.Chapters.Should().HaveCount(3);
        info.Chapters[0].Title.Should().Be("Track 01");
        info.Chapters[0].Start.Should().Be(TimeSpan.Zero);
        info.Chapters[0].End.Should().Be(TimeSpan.FromSeconds(240));
        info.Chapters[1].Title.Should().Be("Track 02");
        info.Chapters[2].Title.Should().Be("Track 03");
        info.Chapters[2].End.Should().Be(TimeSpan.FromSeconds(780));
    }

    [Fact]
    public async Task AnalyzeAsync_UsesProcessRunner_AndParsesResult()
    {
        string json = LoadFixture("fixture-1080p-h264-aac.json");
        Mock<IProcessRunner> mockRunner = new();
        mockRunner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(0, json, "", TimeSpan.FromMilliseconds(50)));

        MediaAnalyzer analyzer = new(
            mockRunner.Object,
            TestStorageFactory.CreateLocal(),
            new() { FfprobePathOverride = "ffprobe" }
        );
        MediaInfo info = await analyzer.AnalyzeAsync("/media/test.mkv");

        info.VideoStreams.Should().HaveCount(1);
        info.AudioStreams.Should().HaveCount(1);
        info.Format.Should().Be("matroska,webm");

        mockRunner.Verify(
            r =>
                r.RunAsync(
                    "ffprobe",
                    It.Is<string[]>(args =>
                        args[args.Length - 1].EndsWith("test.mkv")
                        && args.Contains("-show_streams")
                        && args.Contains("-show_format")
                        && args.Contains("-show_chapters")
                    ),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task AnalyzeAsync_WhenFfprobeFails_Throws()
    {
        Mock<IProcessRunner> mockRunner = new();
        mockRunner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new ProcessResult(1, "", "No such file or directory", TimeSpan.FromMilliseconds(10))
            );

        MediaAnalyzer analyzer = new(
            mockRunner.Object,
            TestStorageFactory.CreateLocal(),
            new() { FfprobePathOverride = "ffprobe" }
        );

        Func<Task> act = async () => await analyzer.AnalyzeAsync("/nonexistent.mkv");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ffprobe failed*");
    }
}
