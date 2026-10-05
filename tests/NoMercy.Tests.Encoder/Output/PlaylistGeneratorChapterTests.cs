// -------- -----------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -------- -----------------------------------------------------------------------

using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Output;

namespace NoMercy.Tests.Encoder.Output;

public class PlaylistGeneratorChapterTests
{
    private const string MediaTitle = "Test.Title";

    private string GenerateWithChapters(OutputPlan plan)
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
    public void GenerateMasterPlaylist_NoChapters_OmitsDaterRanges()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null,
            Chapters: null
        );

        string master = GenerateWithChapters(plan);

        master.Should().NotContain("#EXT-X-DATERANGE");
    }

    [Fact]
    public void GenerateMasterPlaylist_WithChapters_EmitsDaterRanges()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null,
            Chapters:
            [
                new(TimeSpan.Zero, TimeSpan.FromMinutes(5), "Chapter 1"),
                new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), "Chapter 2"),
            ]
        );

        string master = GenerateWithChapters(plan);

        master.Should().Contain("#EXT-X-DATERANGE");
        master.Should().Contain("ID=\"ch0\"");
        master.Should().Contain("ID=\"ch1\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_ChapterDates_ArIso8601()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null,
            Chapters:
            [
                new(TimeSpan.Zero, TimeSpan.FromSeconds(60), "Intro"),
            ]
        );

        string master = GenerateWithChapters(plan);

        master.Should().Contain("START-DATE=\"1970-01-01T00:00:00.000Z\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_ChapterDuration_Correct()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null,
            Chapters:
            [
                new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), "Act 1"),
                new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(150), "Act 2"),
            ]
        );

        string master = GenerateWithChapters(plan);

        master.Should().Contain("DURATION=60");
    }

    [Fact]
    public void GenerateMasterPlaylist_ChapterTitle_EscapesQuotes()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null,
            Chapters:
            [
                new(TimeSpan.Zero, TimeSpan.FromMinutes(5), "Chapter \"1\" with \"quotes\""),
            ]
        );

        string master = GenerateWithChapters(plan);

        master.Should()
            .Contain("X-COM-NOMERCY-CHAPTER-TITLE=\"Chapter \\\"1\\\" with \\\"quotes\\\"\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_MultipleChapters_SequentialIds()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null,
            Chapters:
            [
                new(TimeSpan.FromMinutes(0), TimeSpan.FromMinutes(10), "Chapter 1"),
                new(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), "Chapter 2"),
                new(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), "Chapter 3"),
            ]
        );

        string master = GenerateWithChapters(plan);

        int chapterCount = master.Split("ID=\"ch").Length - 1;
        chapterCount.Should().Be(3);

        master.Should().Contain("ID=\"ch0\"");
        master.Should().Contain("ID=\"ch1\"");
        master.Should().Contain("ID=\"ch2\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_EmptyChapterTitle_GeneratesDefault()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null,
            Chapters:
            [
                new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(60), null),
            ]
        );

        string master = GenerateWithChapters(plan);

        master.Should().Contain("X-COM-NOMERCY-CHAPTER-TITLE=\"Chapter 1\"");
    }

    [Fact]
    public void GenerateMasterPlaylist_ChaptersWithoutChaptersOption_Ignored()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null,
            Chapters: []
        );

        string master = GenerateWithChapters(plan);

        master.Should().NotContain("#EXT-X-DATERANGE");
    }

    [Fact]
    public void GenerateMasterPlaylist_ChapterDurationUsingNextChapterStart()
    {
        OutputPlan plan = new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    1920, 1080, "libx264", 23, 8000, "medium", "high", "4.0", false,
                    "yuv420p", "[v0]", new()
                ),
            ],
            AudioOutputs: [],
            SubtitleOutputs: [],
            Thumbnails: null,
            Chapters:
            [
                new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1000), "Chapter 1"),
                new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1000), "Chapter 2"),
            ]
        );

        string master = GenerateWithChapters(plan);

        string[] dateRangeLines = master.Split('\n').Where(l => l.Contains("ID=\"ch")).ToArray();
        dateRangeLines.Should().HaveCount(2);

        dateRangeLines[0].Should().Contain("DURATION=30");
        dateRangeLines[1].Should().Contain("DURATION=970");
    }
}
