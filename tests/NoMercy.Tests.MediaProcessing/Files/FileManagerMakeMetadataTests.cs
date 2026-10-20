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
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files;

// ---------------------------------------------------------------------------
// MakeMetadata is the method that assembles every hash-list scanner in
// FileManager.Hashing.cs (video/audio/subtitle/font/preview/chapter) into the
// single Metadata record a rescan persists. This drives it end to end
// against a real fixture tree with a PRE-WRITTEN master.m3u8 that already
// advertises every on-disk rendition, so MasterCoversDiskOutput's skip guard
// fires and RebuildHlsMasterFromDiskAsync never needs the real ffprobe
// binary (no IMediaAnalyzer call — verified via the mock's Invocations).
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerMakeMetadataTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Mock<IMediaAnalyzer> _mediaAnalyzer = new();

    public FileManagerMakeMetadataTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-makemeta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private FileManager BuildFileManager()
    {
        Mock<IFileRepository> repoMock = new();
        Mock<IStorageFactory> factoryMock = new();
        return new(
            repoMock.Object,
            factoryMock.Object,
            _mediaAnalyzer.Object,
            TestFilenameParser.Default
        );
    }

    private static IStorage BuildLocalStorage()
    {
        LocalStorageDriver driver = new();
        return new LocalStorage(driver, new StoragePathGuard([], driver));
    }

    private static async Task<Metadata> InvokeMakeMetadata(
        FileManager manager,
        IStorage storage,
        MediaFile item,
        string fileName,
        string baseFolder,
        string hostFolder,
        List<VideoTrack> extraFiles
    )
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "MakeMetadata",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("MakeMetadata not found");

        return await (Task<Metadata>)
            method.Invoke(manager, [storage, item, fileName, baseFolder, hostFolder, extraFiles])!;
    }

    [Fact]
    public async Task MakeMetadata_FullFixtureTree_AssemblesEveryAssetKindWithoutReprobing()
    {
        string hostDir = Path.Combine(_tempRoot, "The.Full.Fixture.(2025).NoMercy");
        Directory.CreateDirectory(hostDir);

        // Video rendition.
        string videoDir = Path.Combine(hostDir, "video_1920x1080_SDR");
        Directory.CreateDirectory(videoDir);
        File.WriteAllText(Path.Combine(videoDir, "video_1920x1080_SDR.m3u8"), "#EXTM3U");
        File.WriteAllBytes(Path.Combine(videoDir, "segment0.ts"), new byte[1000]);

        // Audio rendition.
        string audioDir = Path.Combine(hostDir, "audio_eng_aac");
        Directory.CreateDirectory(audioDir);
        File.WriteAllText(Path.Combine(audioDir, "audio_eng_aac.m3u8"), "#EXTM3U");
        File.WriteAllBytes(Path.Combine(audioDir, "segment0.ts"), new byte[500]);

        // Subtitle.
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllText(Path.Combine(subtitleDir, "Fixture.eng.full.vtt"), "WEBVTT\n");

        // Font.
        string fontsDir = Path.Combine(hostDir, "fonts");
        Directory.CreateDirectory(fontsDir);
        File.WriteAllBytes(Path.Combine(fontsDir, "Arial.ttf"), new byte[64]);

        // Chapters.
        File.WriteAllText(
            Path.Combine(hostDir, "chapters.vtt"),
            "WEBVTT\n\nChapter 1\n00:00:00.000 --> 00:01:00.000\nIntro\n"
        );

        // Preview sprite + thumbnails pair (matching stem so GetExtraFiles
        // would pair them too — MakeMetadata is given the extraFiles list
        // directly here, mirroring what GetExtraFiles produces).
        File.WriteAllBytes(Path.Combine(hostDir, "sprite_320x180.webp"), new byte[200]);
        File.WriteAllText(
            Path.Combine(hostDir, "sprite_320x180.vtt"),
            "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nsprite_320x180.webp#xywh=0,0,320,180\n"
        );

        // Pre-written master that already advertises every rendition above —
        // MasterCoversDiskOutput must see this as complete and skip the
        // reprobe entirely (subtitles/ here is flat files, not language
        // subdirectories, so that half of the check trivially passes).
        string masterPath = Path.Combine(hostDir, "The.Full.Fixture.(2025).NoMercy.m3u8");
        File.WriteAllText(
            masterPath,
            "#EXTM3U\n"
                + "#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080\n"
                + "video_1920x1080_SDR/video_1920x1080_SDR.m3u8\n"
                + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio_aac\",LANGUAGE=\"eng\"\n"
                + "audio_eng_aac/audio_eng_aac.m3u8\n"
        );

        List<VideoTrack> extraFiles =
        [
            new() { File = "/chapters.vtt", Kind = "chapters" },
            new() { File = "/sprite_320x180.webp", Kind = "sprite" },
            new() { File = "/sprite_320x180.vtt", Kind = "thumbnails" },
            new() { File = "/fonts/Arial.ttf", Kind = "fonts" },
        ];

        MediaFile item = new() { Path = hostDir, FFprobe = null };

        FileManager manager = BuildFileManager();
        IStorage storage = BuildLocalStorage();

        Metadata metadata = await InvokeMakeMetadata(
            manager,
            storage,
            item,
            "/The.Full.Fixture.(2025).NoMercy.m3u8",
            "/The.Full.Fixture.(2025).NoMercy",
            hostDir,
            extraFiles
        );

        _mediaAnalyzer
            .Invocations.Should()
            .BeEmpty("the master already covers every on-disk rendition");

        metadata.Video.Should().ContainSingle();
        metadata.Video![0].Width.Should().Be(1920);

        metadata.Audio.Should().ContainSingle();
        metadata.Audio![0].Language.Should().Be("eng");

        metadata.Subtitles.Should().ContainSingle();
        metadata.Subtitles![0].Language.Should().Be("eng");

        metadata.Fonts.Should().ContainSingle();

        metadata.Previews.Should().ContainSingle();
        metadata.Previews![0].Width.Should().Be(320);

        metadata.Chapters.Should().ContainSingle();
        metadata.Chapters![0].Title.Should().Be("Intro");

        metadata.ChapterFile.Should().NotBeNull();
        metadata.ChapterFile!.FileName.Should().Be("/chapters.vtt");

        metadata.FontsFile.Should().NotBeNull();

        // FolderSize sums every catalogued asset's on-disk size — a sanity
        // floor, not an exact byte count (compressed hashes etc. vary).
        metadata.FolderSize.Should().BeGreaterThan(1000 + 500);

        metadata.Type.Should().Be(NoMercy.Database.Models.Media.MediaType.Tv);
    }

    [Fact]
    public async Task MakeMetadata_NoChaptersOrFontsExtraFiles_LeavesThoseFieldsNull()
    {
        string hostDir = Path.Combine(_tempRoot, "No.Extras.(2025).NoMercy");
        Directory.CreateDirectory(hostDir);

        MediaFile item = new() { Path = hostDir, FFprobe = null };
        FileManager manager = BuildFileManager();
        IStorage storage = BuildLocalStorage();

        Metadata metadata = await InvokeMakeMetadata(
            manager,
            storage,
            item,
            "/No.Extras.(2025).NoMercy.m3u8",
            "/No.Extras.(2025).NoMercy",
            hostDir,
            []
        );

        metadata.ChapterFile.Should().BeNull();
        metadata.FontsFile.Should().BeNull();
        metadata.Video.Should().BeEmpty();
        metadata.FolderSize.Should().Be(0);
    }
}
