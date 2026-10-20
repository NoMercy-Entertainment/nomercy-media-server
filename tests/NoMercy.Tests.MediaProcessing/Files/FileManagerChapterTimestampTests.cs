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

using System.Reflection;
using Moq;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Files;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files;

// ---------------------------------------------------------------------------
// FileManager.ParseVttTimestampMs is the private helper ParseChaptersVtt
// (already covered by ChapterVttParserTests.cs against real WEBVTT text)
// delegates to for every timing group. These tests drive it directly to
// exercise the hours/minutes/seconds TryParse failure branches a
// regex-constrained caller can never reach (ChapterTimingRegex only ever
// matches digit sequences), plus the NOTE/STYLE/REGION block-skip branches
// in ParseChaptersVtt itself, and GetChapterHashListAsync end-to-end against
// a real chapter file on disk.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerChapterTimestampTests : IDisposable
{
    private readonly string _tempRoot;

    public FileManagerChapterTimestampTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-chapters-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static int InvokeParseVttTimestampMs(string timestamp)
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "ParseVttTimestampMs",
                BindingFlags.NonPublic | BindingFlags.Static
            ) ?? throw new InvalidOperationException("ParseVttTimestampMs not found");
        return (int)method.Invoke(null, [timestamp])!;
    }

    // -----------------------------------------------------------------------
    // 3-component (HH:MM:SS.mmm) — valid, and each TryParse failure branch.
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseVttTimestampMs_ThreePart_Valid_ReturnsWholeMilliseconds()
    {
        InvokeParseVttTimestampMs("01:02:03.500").Should().Be(3723500);
    }

    [Fact]
    public void ParseVttTimestampMs_ThreePart_HoursUnparseable_ReturnsMinusOne()
    {
        InvokeParseVttTimestampMs("ab:02:03.500").Should().Be(-1);
    }

    [Fact]
    public void ParseVttTimestampMs_ThreePart_MinutesUnparseable_ReturnsMinusOne()
    {
        InvokeParseVttTimestampMs("01:ab:03.500").Should().Be(-1);
    }

    [Fact]
    public void ParseVttTimestampMs_ThreePart_SecondsUnparseable_ReturnsMinusOne()
    {
        InvokeParseVttTimestampMs("01:02:ab").Should().Be(-1);
    }

    // -----------------------------------------------------------------------
    // 2-component (MM:SS.mmm) — valid, and each TryParse failure branch.
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseVttTimestampMs_TwoPart_Valid_ReturnsWholeMilliseconds()
    {
        InvokeParseVttTimestampMs("02:03.500").Should().Be(123500);
    }

    [Fact]
    public void ParseVttTimestampMs_TwoPart_MinutesUnparseable_ReturnsMinusOne()
    {
        InvokeParseVttTimestampMs("ab:03.500").Should().Be(-1);
    }

    [Fact]
    public void ParseVttTimestampMs_TwoPart_SecondsUnparseable_ReturnsMinusOne()
    {
        InvokeParseVttTimestampMs("02:ab").Should().Be(-1);
    }

    // -----------------------------------------------------------------------
    // Neither 2 nor 3 colon-separated parts — the `else return -1` branch.
    // Unreachable from ParseChaptersVtt's real call path (ChapterTimingRegex
    // only ever captures a 2- or 3-part digit group), so only directly
    // testable by calling the helper itself.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("01:02:03:04")]
    [InlineData("05")]
    [InlineData("")]
    public void ParseVttTimestampMs_WrongPartCount_ReturnsMinusOne(string timestamp)
    {
        InvokeParseVttTimestampMs(timestamp).Should().Be(-1);
    }

    // -----------------------------------------------------------------------
    // ParseChaptersVtt: NOTE / STYLE / REGION blocks are metadata, never
    // cues — must be skipped, not mis-parsed as a garbage chapter.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("NOTE this is a comment")]
    [InlineData("STYLE\n::cue { color: white; }")]
    [InlineData("REGION\nid:bottom\nwidth:40%")]
    public void ParseChaptersVtt_MetadataBlock_IsSkipped_NotEmittedAsChapter(string metadataBlock)
    {
        string text =
            "WEBVTT\n\n" + metadataBlock + "\n\n" + "00:00:10.000 --> 00:00:20.000\nReal Chapter\n";

        List<IChapter> chapters = FileManager.ParseChaptersVtt(text);

        chapters.Should().ContainSingle(c => c.Title == "Real Chapter");
    }

    // -----------------------------------------------------------------------
    // GetChapterHashListAsync — the async wrapper that reads a real chapter
    // file off disk and maps ParseChaptersVtt's output onto IChapter DTOs.
    // -----------------------------------------------------------------------

    private static FileManager BuildFileManager()
    {
        Mock<IFileRepository> repoMock = new();
        Mock<IStorageFactory> factoryMock = new();
        Mock<IMediaAnalyzer> mediaAnalyzerMock = new();
        return new(
            repoMock.Object,
            factoryMock.Object,
            mediaAnalyzerMock.Object,
            TestFilenameParser.Default
        );
    }

    private static IStorage BuildLocalStorage()
    {
        LocalStorageDriver driver = new();
        return new LocalStorage(driver, new StoragePathGuard([], driver));
    }

    private static async Task<List<IChapter>> InvokeGetChapterHashListAsync(
        FileManager manager,
        IStorage storage,
        string hostFolder,
        string file
    )
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "GetChapterHashListAsync",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("GetChapterHashListAsync not found");

        return await (Task<List<IChapter>>)method.Invoke(manager, [storage, hostFolder, file])!;
    }

    [Fact]
    public async Task GetChapterHashListAsync_RealChapterFile_ReturnsParsedChapters()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.Chapters");
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(
            Path.Combine(hostDir, "chapters.vtt"),
            "WEBVTT\n\nChapter 1\n00:00:00.000 --> 00:01:00.000\nIntro\n"
        );

        List<IChapter> result = await InvokeGetChapterHashListAsync(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir,
            "chapters.vtt"
        );

        result.Should().ContainSingle();
        result[0].Title.Should().Be("Intro");
        result[0].StartTime.Should().Be(0);
        result[0].EndTime.Should().Be(60000);
    }

    [Fact]
    public async Task GetChapterHashListAsync_EmptyChapterFile_ReturnsEmptyList()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.EmptyChapters");
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(Path.Combine(hostDir, "chapters.vtt"), "WEBVTT\n");

        List<IChapter> result = await InvokeGetChapterHashListAsync(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir,
            "chapters.vtt"
        );

        result.Should().BeEmpty();
    }
}
