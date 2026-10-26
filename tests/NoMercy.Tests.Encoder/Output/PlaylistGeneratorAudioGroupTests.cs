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

using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;

namespace NoMercy.Tests.Encoder.Output;

public partial class PlaylistGeneratorAudioGroupTests
{
    [System.Text.RegularExpressions.GeneratedRegex(
        "#EXT-X-MEDIA:TYPE=AUDIO",
        System.Text.RegularExpressions.RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial System.Text.RegularExpressions.Regex AudioMediaEntry();

    [System.Text.RegularExpressions.GeneratedRegex(
        "DEFAULT=YES",
        System.Text.RegularExpressions.RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial System.Text.RegularExpressions.Regex DefaultYes();

    private const string MediaTitle = "Test.Title";

    private string Generate(OutputPlan plan)
    {
        Dictionary<string, VariantMetrics> videoMetrics = plan.VideoOutputs.ToDictionary(
            v => VideoVariantKey(v),
            _ => new VariantMetrics(5_000_000, 4_500_000)
        );

        Dictionary<string, VariantMetrics> audioMetrics = plan.AudioOutputs.ToDictionary(
            a => AudioVariantKey(a),
            _ => new VariantMetrics(192_000, 180_000)
        );

        PlaylistGenerator generator = new();
        return generator.GenerateMasterPlaylist(plan, MediaTitle, videoMetrics, audioMetrics);
    }

    private static string VideoVariantKey(VideoOutputPlan video) =>
        TemplateResolver.Resolve(
            video.PlaylistNameTemplate,
            TemplateResolver.VideoTokens(video.Width, video.Height, video.IsHdrOutput)
        );

    private static string AudioVariantKey(AudioOutputPlan audio) =>
        TemplateResolver.Resolve(
            audio.PlaylistNameTemplate,
            TemplateResolver.AudioTokens(audio.Language ?? "und", audio.CodecToken, audio.Channels)
        );

    [Fact]
    public void GenerateMasterPlaylist_VideoOnlyNoAudio_OmitsAudioGroupAndAudioAttribute()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().NotContain("#EXT-X-MEDIA:TYPE=AUDIO");
        master.Should().NotContain("AUDIO=");
        master.Should().Contain("CLOSED-CAPTIONS=NONE");
    }

    [Fact]
    public void GenerateMasterPlaylist_WithAudioRendition_EmitsAudioGroupAndAttribute()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO");
        master.Should().Contain("GROUP-ID=\"audio_aac\"");
        master.Should().Contain("LANGUAGE=\"eng\"");
        master.Should().Contain("AUDIO=\"audio_aac\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_MultipleAudioCodecs_KeepsDistinctGroupIds()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("GROUP-ID=\"audio_aac\"");
        master.Should().MatchRegex(@"AUDIO=""audio_aac""");
        master.Should().NotContain("audio_opus");
        master.Should().NotContain("audio_eac3");
    }

    [Fact]
    public void GenerateMasterPlaylist_OpusAudio_UsesOpusGroupId()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [new("libopus", 128, 2, 48000, StreamAction.Transcode, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("GROUP-ID=\"audio_opus\"");
        master.Should().MatchRegex(@"AUDIO=""audio_opus""");
    }

    [Fact]
    public void GenerateMasterPlaylist_Eac3Audio_UsesEac3GroupId()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [new("eac3", 384, 6, 48000, StreamAction.Transcode, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("GROUP-ID=\"audio_eac3\"");
        master.Should().MatchRegex(@"AUDIO=""audio_eac3""");
    }

    [Fact]
    public void GenerateMasterPlaylist_AudioWithZeroBandwidth_OmitsFromGroupAndDoesNotEmitAudioAttribute()
    {
        PlaylistGenerator generator = new();
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        Dictionary<string, VariantMetrics> vidMetrics = new()
        {
            [VideoVariantKey(plan.VideoOutputs[0])] = new(5_000_000, 4_500_000),
        };

        Dictionary<string, VariantMetrics> audMetrics = new()
        {
            [AudioVariantKey(plan.AudioOutputs[0])] = new(0, 0),
        };

        string master = generator.GenerateMasterPlaylist(plan, MediaTitle, vidMetrics, audMetrics);

        master.Should().NotContain("#EXT-X-MEDIA:TYPE=AUDIO");
        master.Should().NotContain("AUDIO=");
    }

    [Fact]
    public void GenerateMasterPlaylist_MultipleAudioLanguages_EachEmitsOwnMediaLine()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs:
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0"),
                new("aac", 192, 2, 48000, StreamAction.Transcode, "fra", "0:a:1"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        Dictionary<string, VariantMetrics> videoMetrics = plan.VideoOutputs.ToDictionary(
            v => VideoVariantKey(v),
            _ => new VariantMetrics(5_000_000, 4_500_000)
        );

        Dictionary<string, VariantMetrics> audioMetrics = new()
        {
            [AudioVariantKey(plan.AudioOutputs[0])] = new(192_000, 180_000),
            [AudioVariantKey(plan.AudioOutputs[1])] = new(192_000, 180_000),
        };

        PlaylistGenerator generator = new();
        string master = generator.GenerateMasterPlaylist(
            plan,
            MediaTitle,
            videoMetrics,
            audioMetrics
        );

        int audioMediaCount = AudioMediaEntry().Matches(master).Count;
        audioMediaCount.Should().Be(2);

        master.Should().Contain("LANGUAGE=\"eng\"");
        master.Should().Contain("LANGUAGE=\"fra\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_AudioCopyAction_IncludedInGroup()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [new("aac", 0, 2, 48000, StreamAction.Copy, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO");
        master.Should().Contain("GROUP-ID=\"audio_aac\"");
        master.Should().Contain("LANGUAGE=\"eng\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_AudioOtherAction_NotIncludedInGroup()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs: [new("aac", 192, 2, 48000, StreamAction.Drop, "eng", "0:a:0")],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().NotContain("LANGUAGE=\"eng\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_DefaultAudioFlag_FirstRenditionOnly()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs:
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:0"),
                new("aac", 192, 2, 48000, StreamAction.Transcode, "fra", "0:a:1"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        Dictionary<string, VariantMetrics> videoMetrics = plan.VideoOutputs.ToDictionary(
            v => VideoVariantKey(v),
            _ => new VariantMetrics(5_000_000, 4_500_000)
        );

        Dictionary<string, VariantMetrics> audioMetrics = new()
        {
            [AudioVariantKey(plan.AudioOutputs[0])] = new(192_000, 180_000),
            [AudioVariantKey(plan.AudioOutputs[1])] = new(192_000, 180_000),
        };

        PlaylistGenerator generator = new();
        string master = generator.GenerateMasterPlaylist(
            plan,
            MediaTitle,
            videoMetrics,
            audioMetrics
        );

        int defaultCount = DefaultYes().Matches(master).Count;
        defaultCount.Should().Be(1);

        master.Should().Contain("LANGUAGE=\"eng\",AUTOSELECT=YES,DEFAULT=YES");
        master.Should().Contain("LANGUAGE=\"fra\",AUTOSELECT=YES,DEFAULT=NO");
    }

    [Fact]
    public void GenerateMasterPlaylist_SourceDefaultIsNotFirst_MarksThatRenditionDefault()
    {
        // Keira's bug: episodes resumed in Portuguese / swapped en to fr because
        // DEFAULT=YES followed ffprobe stream order instead of the release's own
        // default-audio disposition, and that order differs file by file.
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs:
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "por", "0:a:0"),
                new("aac", 192, 2, 48000, StreamAction.Transcode, "jpn", "0:a:1")
                {
                    IsSourceDefault = true,
                },
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:2"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("LANGUAGE=\"jpn\",AUTOSELECT=YES,DEFAULT=YES");
        master.Should().Contain("LANGUAGE=\"por\",AUTOSELECT=YES,DEFAULT=NO");
        master.Should().Contain("LANGUAGE=\"eng\",AUTOSELECT=YES,DEFAULT=NO");
    }

    [Fact]
    public void GenerateMasterPlaylist_NoSourceDefault_FallsBackToFirstRendition()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920,
                    1080,
                    "libx264",
                    23,
                    8000,
                    "medium",
                    "high",
                    "4.0",
                    false,
                    "yuv420p",
                    "[v0]",
                    new()
                ),
            ],
            AudioOutputs:
            [
                new("aac", 192, 2, 48000, StreamAction.Transcode, "por", "0:a:0"),
                new("aac", 192, 2, 48000, StreamAction.Transcode, "eng", "0:a:1"),
            ],
            SubtitleOutputs: [],
            Thumbnails: null
        );

        string master = Generate(plan);

        master.Should().Contain("LANGUAGE=\"por\",AUTOSELECT=YES,DEFAULT=YES");
        master.Should().Contain("LANGUAGE=\"eng\",AUTOSELECT=YES,DEFAULT=NO");
    }
}
