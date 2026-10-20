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
// Remaining branch gaps in GetVideoHashList / GetAudioHashList /
// GetSubtitleHashList / ParseChaptersVtt / ComputeFileHash that the earlier
// test files (FileManagerAssetHashListTests, FileManagerHashListTests,
// FileManagerMasterRebuildTests) don't exercise: the "rendition dir exists
// but is empty/malformed" paths a live scan hits whenever an encode is
// interrupted mid-write, and ComputeFileHash's stat-failure fallback.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerHashingBranchGapTests : IDisposable
{
    private readonly string _tempRoot;

    public FileManagerHashingBranchGapTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-branchgap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

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

    private static object InvokePrivate(string methodName, object?[] args, bool isStatic = false)
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                methodName,
                (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.NonPublic
            ) ?? throw new InvalidOperationException($"{methodName} not found");
        return method.Invoke(isStatic ? null : BuildFileManager(), args)!;
    }

    private static List<IVideo> InvokeGetVideoHashList(IStorage storage, string hostFolder) =>
        (List<IVideo>)
            typeof(FileManager)
                .GetMethod("GetVideoHashList", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(BuildFileManager(), [storage, hostFolder])!;

    private static List<IAudio> InvokeGetAudioHashList(IStorage storage, string hostFolder) =>
        (List<IAudio>)
            typeof(FileManager)
                .GetMethod("GetAudioHashList", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(BuildFileManager(), [storage, hostFolder])!;

    private static List<ISubtitle> InvokeGetSubtitleHashList(IStorage storage, string hostFolder) =>
        (List<ISubtitle>)
            typeof(FileManager)
                .GetMethod("GetSubtitleHashList", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(BuildFileManager(), [storage, hostFolder])!;

    // -----------------------------------------------------------------------
    // GetVideoHashList
    // -----------------------------------------------------------------------

    [Fact]
    public void GetVideoHashList_HostFolderMissing_ReturnsEmpty()
    {
        string hostDir = Path.Combine(_tempRoot, "does-not-exist");

        List<IVideo> result = InvokeGetVideoHashList(BuildLocalStorage(), hostDir);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetVideoHashList_VideoDirNameDoesNotMatchWidthHeightPattern_IsSkipped()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.BadVideoDir");
        Directory.CreateDirectory(Path.Combine(hostDir, "video_bogus"));
        File.WriteAllText(Path.Combine(hostDir, "video_bogus", "video_bogus.m3u8"), "#EXTM3U");

        List<IVideo> result = InvokeGetVideoHashList(BuildLocalStorage(), hostDir);

        result
            .Should()
            .BeEmpty("a video_* dir name that doesn't parse as WxH must be skipped, not throw");
    }

    [Fact]
    public void GetVideoHashList_MatchingDirWithNestedSubdirButNoPlaylist_IsSkipped()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.NoPlaylist");
        string videoDir = Path.Combine(hostDir, "video_1920x1080");
        Directory.CreateDirectory(videoDir);
        // A nested subdirectory forces the playlist lookup's `!e.IsDirectory`
        // guard to see a real directory entry, not just files.
        Directory.CreateDirectory(Path.Combine(videoDir, "stray-subdir"));
        File.WriteAllBytes(Path.Combine(videoDir, "segment0.ts"), new byte[8]);
        // No .m3u8 file at all — playlist lookup must come back null.

        List<IVideo> result = InvokeGetVideoHashList(BuildLocalStorage(), hostDir);

        result
            .Should()
            .BeEmpty("a rendition dir with no playlist (interrupted encode) must be skipped");
    }

    [Fact]
    public void GetVideoHashList_MatchingDirWithNestedSubdirAndPlaylist_IsIncluded()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.WithSubdirAndPlaylist");
        string videoDir = Path.Combine(hostDir, "video_1920x1080");
        Directory.CreateDirectory(videoDir);
        Directory.CreateDirectory(Path.Combine(videoDir, "stray-subdir"));
        File.WriteAllText(Path.Combine(videoDir, "video_1920x1080.m3u8"), "#EXTM3U");
        File.WriteAllBytes(Path.Combine(videoDir, "segment0.ts"), new byte[8]);

        List<IVideo> result = InvokeGetVideoHashList(BuildLocalStorage(), hostDir);

        result.Should().ContainSingle();
        result[0].Width.Should().Be(1920);
        result[0].Height.Should().Be(1080);
    }

    // -----------------------------------------------------------------------
    // GetAudioHashList
    // -----------------------------------------------------------------------

    [Fact]
    public void GetAudioHashList_HostFolderMissing_ReturnsEmpty()
    {
        string hostDir = Path.Combine(_tempRoot, "does-not-exist-audio");

        List<IAudio> result = InvokeGetAudioHashList(BuildLocalStorage(), hostDir);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetAudioHashList_AudioDirNameDoesNotMatchLanguagePattern_IsSkipped()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.BadAudioDir");
        // "toolong" is more than the 2-3 letter language token the regex allows.
        Directory.CreateDirectory(Path.Combine(hostDir, "audio_toolonglanguage"));
        File.WriteAllText(Path.Combine(hostDir, "audio_toolonglanguage", "x.m3u8"), "#EXTM3U");

        List<IAudio> result = InvokeGetAudioHashList(BuildLocalStorage(), hostDir);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetAudioHashList_MatchingDirWithNestedSubdirButNoPlaylist_IsSkipped()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.AudioNoPlaylist");
        string audioDir = Path.Combine(hostDir, "audio_eng_aac");
        Directory.CreateDirectory(audioDir);
        Directory.CreateDirectory(Path.Combine(audioDir, "stray-subdir"));
        File.WriteAllBytes(Path.Combine(audioDir, "segment0.ts"), new byte[8]);

        List<IAudio> result = InvokeGetAudioHashList(BuildLocalStorage(), hostDir);

        result.Should().BeEmpty("an audio rendition dir with no playlist must be skipped");
    }

    [Fact]
    public void GetAudioHashList_OldNamingNoCodecSuffix_DefaultsToAac()
    {
        // Direct GetAudioHashList-level regression pin for the old-naming
        // (no `_<codec>` token) audio dir — the master-rebuild path already
        // pins this end to end; this covers the codec ternary's false branch
        // in GetAudioHashList itself.
        string hostDir = Path.Combine(_tempRoot, "Show.OldNamingAudio");
        string audioDir = Path.Combine(hostDir, "audio_jpn");
        Directory.CreateDirectory(audioDir);
        File.WriteAllText(Path.Combine(audioDir, "audio_jpn.m3u8"), "#EXTM3U");

        List<IAudio> result = InvokeGetAudioHashList(BuildLocalStorage(), hostDir);

        result.Should().ContainSingle();
        result[0].Language.Should().Be("jpn");
        result[0].Codec.Should().Be("aac");
    }

    // -----------------------------------------------------------------------
    // GetSubtitleHashList — a file in subtitles/ that doesn't match the
    // {lang}.{type}.{ext} shape at all (not a bitmap rejection — no match).
    // -----------------------------------------------------------------------

    [Fact]
    public void GetSubtitleHashList_UnrecognizedFileName_IsSkipped()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.JunkInSubs");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllText(Path.Combine(subtitleDir, "README.txt"), "not a subtitle");

        List<ISubtitle> result = InvokeGetSubtitleHashList(BuildLocalStorage(), hostDir);

        result.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // ParseChaptersVtt: a cue whose block is JUST the timing line (no
    // preceding id line, no following title line) — the title ternary's
    // false branch.
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseChaptersVtt_TimingLineWithNoTitleLine_ProducesEmptyTitle()
    {
        // Starts at 0 so NormalizeChapters has nothing to prepend — isolates
        // the title ternary's false branch (no line after the timing line).
        const string text = "WEBVTT\n\n00:00:00.000 --> 00:00:10.000\n";

        List<IChapter> chapters = FileManager.ParseChaptersVtt(text);

        chapters.Should().ContainSingle();
        chapters[0].Title.Should().BeEmpty();
        chapters[0].StartTime.Should().Be(0);
        chapters[0].EndTime.Should().Be(10000);
    }

    // -----------------------------------------------------------------------
    // ComputeFileHash: storage.LastModified throwing must not blow up the
    // hash — falls back to modifiedTicks = 0, still hashing size+ticks.
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeFileHash_LastModifiedThrows_FallsBackToZeroTicksInsteadOfThrowing()
    {
        Mock<IStorage> storage = new();
        storage.Setup(s => s.SizeOrZero("some/path")).Returns(42);
        storage.Setup(s => s.LastModified("some/path")).Throws(new IOException("stat failed"));

        MethodInfo method =
            typeof(FileManager).GetMethod(
                "ComputeFileHash",
                BindingFlags.NonPublic | BindingFlags.Static
            ) ?? throw new InvalidOperationException("ComputeFileHash not found");

        string hash = (string)method.Invoke(null, [storage.Object, "some/path"])!;

        hash.Should().NotBeNullOrEmpty();
        hash.Should().HaveLength(64, "SHA-256 hex digest");
    }
}
