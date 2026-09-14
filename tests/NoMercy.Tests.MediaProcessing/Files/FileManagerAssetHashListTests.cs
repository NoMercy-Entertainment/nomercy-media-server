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
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NoMercy.Tests.MediaProcessing.Files;

// ---------------------------------------------------------------------------
// Real on-disk fixtures for the FileManager.Hashing asset scanners that back
// a rescan's Metadata: subtitles (text vs. bitmap sidecar rejection),
// preview sprite/thumbnail pairing, fonts, and the VTT dimension/image-size
// helpers. Every case builds a real temp directory tree and drives the
// private scanner methods through LocalStorage exactly like a live scan
// would — no mock stands in for the unit under test.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerAssetHashListTests : IDisposable
{
    private readonly string _tempRoot;

    public FileManagerAssetHashListTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-assets-{Guid.NewGuid():N}");
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
        Mock<IStorageDriver> driverMock = new();
        Mock<IMediaAnalyzer> mediaAnalyzerMock = new();
        return new(
            repoMock.Object,
            factoryMock.Object,
            driverMock.Object,
            mediaAnalyzerMock.Object,
            TestFilenameParser.Default
        );
    }

    private static IStorage BuildLocalStorage()
    {
        LocalStorageDriver driver = new();
        return new LocalStorage(driver, new StoragePathGuard([], driver));
    }

    private static object InvokePrivate(FileManager manager, string methodName, object?[] args)
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException($"{methodName} not found");
        return method.Invoke(manager, args)!;
    }

    private static List<ISubtitle> InvokeGetSubtitleHashList(
        FileManager manager,
        IStorage storage,
        string hostFolder
    ) => (List<ISubtitle>)InvokePrivate(manager, "GetSubtitleHashList", [storage, hostFolder]);

    private static List<IFont> InvokeGetFontHashList(
        FileManager manager,
        IStorage storage,
        string hostFolder
    ) => (List<IFont>)InvokePrivate(manager, "GetFontHashList", [storage, hostFolder]);

    private static List<IPreview> InvokeGetPreviewHashList(
        FileManager manager,
        IStorage storage,
        string hostFolder,
        List<VideoTrack> extraFiles
    ) =>
        (List<IPreview>)
            InvokePrivate(manager, "GetPreviewHashList", [storage, hostFolder, extraFiles]);

    private static List<VideoTrack> InvokeGetExtraFiles(IStorage storage, string hostFolder) =>
        (List<VideoTrack>)
            typeof(FileManager)
                .GetMethod("GetExtraFiles", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [storage, hostFolder])!;

    private static List<Subtitle> InvokeGetSubtitles(IStorage storage, string hostFolder) =>
        (List<Subtitle>)
            typeof(FileManager)
                .GetMethod("GetSubtitles", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [storage, hostFolder])!;

    private static (int Width, int Height) InvokeGetImageDimensions(string filePath)
    {
        object result = typeof(FileManager)
            .GetMethod("GetImageDimensions", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [filePath])!;
        return ((int, int))result;
    }

    private static (int Width, int Height) InvokeGetImageDimensionsFromVtt(
        IStorage storage,
        string filePath
    )
    {
        object result = typeof(FileManager)
            .GetMethod("GetImageDimensionsFromVtt", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [storage, filePath])!;
        return ((int, int))result;
    }

    // -----------------------------------------------------------------------
    // GetSubtitleHashList — the Metadata-facing subtitle scanner.
    // -----------------------------------------------------------------------

    [Fact]
    public void GetSubtitleHashList_NoSubtitlesFolder_ReturnsEmpty()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.NoSubs");
        Directory.CreateDirectory(hostDir);

        List<ISubtitle> result = InvokeGetSubtitleHashList(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir
        );

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetSubtitleHashList_TextSubtitle_IsIncludedWithLanguageTypeAndCodec()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.WithSubs");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllText(Path.Combine(subtitleDir, "Movie.eng.full.vtt"), "WEBVTT\n");

        List<ISubtitle> result = InvokeGetSubtitleHashList(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir
        );

        result.Should().ContainSingle();
        result[0].Language.Should().Be("eng");
        result[0].Type.Should().Be("full");
        result[0].Codec.Should().Be("vtt");
        result[0].FileName.Should().Be("/subtitles/Movie.eng.full.vtt");
    }

    [Theory]
    [InlineData("sup")]
    [InlineData("idx")]
    [InlineData("vob")]
    [InlineData("mks")]
    public void GetSubtitleHashList_BitmapSidecarExtensions_AreExcluded(string bitmapExtension)
    {
        string hostDir = Path.Combine(_tempRoot, $"Movie.Bitmap.{bitmapExtension}");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllBytes(Path.Combine(subtitleDir, $"Movie.eng.full.{bitmapExtension}"), [0x00]);

        List<ISubtitle> result = InvokeGetSubtitleHashList(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir
        );

        result.Should().BeEmpty("bitmap sidecars cannot be streamed as HLS text sidecars");
    }

    [Theory]
    [InlineData("alt")]
    [InlineData("sdh")]
    [InlineData("forced")]
    [InlineData("sign")]
    [InlineData("song")]
    [InlineData("commentary")]
    [InlineData("director")]
    public void GetSubtitleHashList_AnyTypeToken_Matches(string type)
    {
        // Regression: the matcher used to only accept type in
        // (sign, song, full) — every "alt"/"sdh"/"forced" subtitle silently
        // vanished from the track list.
        string hostDir = Path.Combine(_tempRoot, $"Movie.Type.{type}");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllText(Path.Combine(subtitleDir, $"Movie.eng.{type}.srt"), "1\n");

        List<ISubtitle> result = InvokeGetSubtitleHashList(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir
        );

        result.Should().ContainSingle();
        result[0].Type.Should().Be(type);
    }

    [Fact]
    public void GetSubtitleHashList_TwoCharLanguageCode_Matches()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.TwoCharLang");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllText(Path.Combine(subtitleDir, "Movie.en.full.vtt"), "WEBVTT\n");

        List<ISubtitle> result = InvokeGetSubtitleHashList(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir
        );

        result.Should().ContainSingle();
        result[0].Language.Should().Be("en");
    }

    // -----------------------------------------------------------------------
    // GetFontHashList
    // -----------------------------------------------------------------------

    [Fact]
    public void GetFontHashList_NoFontsFolder_ReturnsEmpty()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.NoFonts");
        Directory.CreateDirectory(hostDir);

        List<IFont> result = InvokeGetFontHashList(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir
        );

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetFontHashList_FontsPresent_ReturnsEachWithHashAndSize()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.WithFonts");
        string fontsDir = Path.Combine(hostDir, "fonts");
        Directory.CreateDirectory(fontsDir);
        File.WriteAllBytes(Path.Combine(fontsDir, "Arial.ttf"), new byte[128]);
        File.WriteAllBytes(Path.Combine(fontsDir, "Comic.otf"), new byte[64]);

        List<IFont> result = InvokeGetFontHashList(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir
        );

        result.Should().HaveCount(2);
        result.Should().Contain(f => f.FileName == "/fonts/Arial.ttf" && f.FileSize == 128);
        result.Should().Contain(f => f.FileName == "/fonts/Comic.otf" && f.FileSize == 64);
        result.Should().OnlyContain(f => !string.IsNullOrEmpty(f.FileHash));
    }

    // -----------------------------------------------------------------------
    // GetImageDimensions / GetImageDimensionsFromVtt
    // -----------------------------------------------------------------------

    [Fact]
    public void GetImageDimensions_RealPngFile_ReturnsItsActualSize()
    {
        string filePath = Path.Combine(_tempRoot, "sprite.png");
        WritePng(filePath, 64, 32);

        (int width, int height) = InvokeGetImageDimensions(filePath);

        width.Should().Be(64);
        height.Should().Be(32);
    }

    [Fact]
    public void GetImageDimensionsFromVtt_ParsesXywhFromVttContents()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.Thumbs");
        Directory.CreateDirectory(hostDir);
        string vttPath = Path.Combine(hostDir, "thumbs.vtt");
        File.WriteAllText(
            vttPath,
            "WEBVTT\n\n" + "00:00:00.000 --> 00:00:05.000\n" + "sprite.jpg#xywh=0,0,320,180\n"
        );

        (int width, int height) = InvokeGetImageDimensionsFromVtt(BuildLocalStorage(), vttPath);

        width.Should().Be(320);
        height.Should().Be(180);
    }

    [Fact]
    public void GetImageDimensionsFromVtt_NoXywhToken_ReturnsZeroZero()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.NoXywh");
        Directory.CreateDirectory(hostDir);
        string vttPath = Path.Combine(hostDir, "thumbs.vtt");
        File.WriteAllText(vttPath, "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nsprite.jpg\n");

        (int width, int height) = InvokeGetImageDimensionsFromVtt(BuildLocalStorage(), vttPath);

        width.Should().Be(0);
        height.Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // GetPreviewHashList — pairs a sprite (Kind="sprite") with its matching
    // thumbnails VTT (Kind="thumbnails") by position (the sprite/thumbnails
    // extraFiles lists are produced index-aligned by GetExtraFiles).
    // -----------------------------------------------------------------------

    [Fact]
    public void GetPreviewHashList_SpriteAndThumbnailPair_ProducesOnePreviewWithDimensions()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.Previews");
        Directory.CreateDirectory(hostDir);
        WritePng(Path.Combine(hostDir, "sprite_320x180.webp"), 320, 180);
        File.WriteAllText(
            Path.Combine(hostDir, "thumbs_320x180.vtt"),
            "WEBVTT\n\n00:00:00.000 --> 00:00:05.000\nsprite_320x180.webp#xywh=0,0,320,180\n"
        );

        List<VideoTrack> extraFiles =
        [
            new() { File = "/sprite_320x180.webp", Kind = "sprite" },
            new() { File = "/thumbs_320x180.vtt", Kind = "thumbnails" },
        ];

        List<IPreview> result = InvokeGetPreviewHashList(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir,
            extraFiles
        );

        result.Should().ContainSingle();
        result[0].ImageFileName.Should().Be("/sprite_320x180.webp");
        result[0].TimeFileName.Should().Be("/thumbs_320x180.vtt");
        result[0].Width.Should().Be(320);
        result[0].Height.Should().Be(180);
        result[0].ImageFileSize.Should().BeGreaterThan(0);
        result[0].TimeFileSize.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetPreviewHashList_NoSpriteOrThumbnailEntries_ReturnsEmpty()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.NoPreviews");
        Directory.CreateDirectory(hostDir);

        List<IPreview> result = InvokeGetPreviewHashList(
            BuildFileManager(),
            BuildLocalStorage(),
            hostDir,
            []
        );

        result.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // GetExtraFiles — chapter/skipper/sprite/thumbnail/font classification,
    // and the stale-VTT-without-matching-sprite exclusion.
    // -----------------------------------------------------------------------

    [Fact]
    public void GetExtraFiles_ClassifiesChapterSkipperSpriteThumbnailAndFonts()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.Extras");
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(Path.Combine(hostDir, "chapters.vtt"), "WEBVTT\n");
        File.WriteAllText(Path.Combine(hostDir, "skipper.json"), "{}");
        File.WriteAllBytes(Path.Combine(hostDir, "sprite_320x180.webp"), new byte[16]);
        // Same stem as the webp ("sprite_320x180") — GetExtraFiles only
        // registers a thumbnails VTT when it has a matching sprite webp.
        File.WriteAllText(Path.Combine(hostDir, "sprite_320x180.vtt"), "WEBVTT\n");
        File.WriteAllText(Path.Combine(hostDir, "fonts.tar"), "x");

        List<VideoTrack> tracks = InvokeGetExtraFiles(BuildLocalStorage(), hostDir);

        tracks.Should().Contain(t => t.Kind == "chapters" && t.File == "/chapters.vtt");
        tracks.Should().Contain(t => t.Kind == "skippers" && t.File == "/skipper.json");
        tracks.Should().Contain(t => t.Kind == "sprite" && t.File == "/sprite_320x180.webp");
        tracks.Should().Contain(t => t.Kind == "thumbnails" && t.File == "/sprite_320x180.vtt");
        tracks.Should().Contain(t => t.Kind == "fonts" && t.File == "/fonts.tar");
    }

    [Fact]
    public void GetExtraFiles_StaleVttWithoutMatchingSpriteStem_IsExcluded()
    {
        // Regression: a re-encode at a different sprite dimension leaves the
        // old VTT behind (thumbs_320x178.vtt) alongside the live sprite
        // (thumbs_320x180.webp). The stale VTT must not be registered — the
        // player would follow its cues to a 404.
        string hostDir = Path.Combine(_tempRoot, "Movie.StaleVtt");
        Directory.CreateDirectory(hostDir);
        File.WriteAllBytes(Path.Combine(hostDir, "thumbs_320x180.webp"), new byte[16]);
        File.WriteAllText(Path.Combine(hostDir, "thumbs_320x178.vtt"), "WEBVTT\n");

        List<VideoTrack> tracks = InvokeGetExtraFiles(BuildLocalStorage(), hostDir);

        tracks.Should().Contain(t => t.Kind == "sprite");
        tracks.Should().NotContain(t => t.Kind == "thumbnails");
    }

    [Fact]
    public void GetExtraFiles_LegacySpriteSheetWithPreviewsVtt_IsPaired()
    {
        // The older encoder wrote the pair under two different names. Judged on
        // stem alone the cue file is thrown away, and the client is left with a
        // sheet it cannot read: the scrub bubble shows the time and no image.
        string hostDir = Path.Combine(_tempRoot, "Movie.LegacyPreviews");
        Directory.CreateDirectory(hostDir);
        File.WriteAllBytes(Path.Combine(hostDir, "sprite.webp"), new byte[16]);
        File.WriteAllText(Path.Combine(hostDir, "previews.vtt"), "WEBVTT\n");

        List<VideoTrack> tracks = InvokeGetExtraFiles(BuildLocalStorage(), hostDir);

        tracks.Should().Contain(t => t.Kind == "sprite" && t.File == "/sprite.webp");
        tracks.Should().Contain(t => t.Kind == "thumbnails" && t.File == "/previews.vtt");
    }

    [Fact]
    public void GetExtraFiles_EmptySheetFromAnInterruptedRebuild_IsIgnored()
    {
        // A preview rebuild writes its new sheet only when ffmpeg finishes. Stopped
        // mid-run it leaves an empty thumbs_320x180.webp beside the working pair,
        // and registering that empty sheet cost the title its scrub preview.
        string hostDir = Path.Combine(_tempRoot, "Movie.InterruptedRebuild");
        Directory.CreateDirectory(hostDir);
        File.WriteAllBytes(Path.Combine(hostDir, "sprite.webp"), new byte[16]);
        File.WriteAllText(Path.Combine(hostDir, "previews.vtt"), "WEBVTT\n");
        File.WriteAllBytes(Path.Combine(hostDir, "thumbs_320x180.webp"), []);

        List<VideoTrack> tracks = InvokeGetExtraFiles(BuildLocalStorage(), hostDir);

        tracks
            .Where(t => t.Kind == "sprite")
            .Should()
            .ContainSingle()
            .Which.File.Should()
            .Be("/sprite.webp");
        tracks.Should().Contain(t => t.Kind == "thumbnails" && t.File == "/previews.vtt");
    }

    [Fact]
    public void GetExtraFiles_PreviewsVttWithoutASpriteSheet_IsExcluded()
    {
        // The legacy pairing is by name, so it must still require the sheet it
        // names — a lone previews.vtt points its cues at nothing.
        string hostDir = Path.Combine(_tempRoot, "Movie.LonePreviews");
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(Path.Combine(hostDir, "previews.vtt"), "WEBVTT\n");

        List<VideoTrack> tracks = InvokeGetExtraFiles(BuildLocalStorage(), hostDir);

        tracks.Should().NotContain(t => t.Kind == "thumbnails");
    }

    // -----------------------------------------------------------------------
    // GetSubtitles — the DB-facing (lightweight, no hash) subtitle list, and
    // its orphaned-bitmap detection.
    // -----------------------------------------------------------------------

    [Fact]
    public void GetSubtitles_NoSubtitlesFolder_ReturnsEmpty()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.NoSubsDb");
        Directory.CreateDirectory(hostDir);

        List<Subtitle> result = InvokeGetSubtitles(BuildLocalStorage(), hostDir);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetSubtitles_TextSubtitle_IsIncluded()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.SubsDb");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllText(Path.Combine(subtitleDir, "Movie.eng.full.ass"), "[Script Info]\n");

        List<Subtitle> result = InvokeGetSubtitles(BuildLocalStorage(), hostDir);

        result.Should().ContainSingle();
        result[0].Language.Should().Be("eng");
        result[0].Type.Should().Be("full");
        result[0].Ext.Should().Be("ass");
    }

    [Fact]
    public void GetSubtitles_BitmapWithoutSiblingVtt_IsExcludedFromResult()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.OrphanBitmap");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllBytes(Path.Combine(subtitleDir, "Movie.jpn.full.sup"), [0x00]);

        List<Subtitle> result = InvokeGetSubtitles(BuildLocalStorage(), hostDir);

        result.Should().BeEmpty("a bitmap sidecar is never itself a playable text track");
    }

    [Fact]
    public void GetSubtitles_BitmapWithSiblingVtt_SiblingIsIncludedBitmapIsNot()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.OcrPaired");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllBytes(Path.Combine(subtitleDir, "Movie.jpn.full.sup"), [0x00]);
        File.WriteAllText(Path.Combine(subtitleDir, "Movie.jpn.full.vtt"), "WEBVTT\n");

        List<Subtitle> result = InvokeGetSubtitles(BuildLocalStorage(), hostDir);

        result.Should().ContainSingle();
        result[0].Ext.Should().Be("vtt");
    }

    [Fact]
    public void GetSubtitles_UnrecognizedFileNameAlongsideRealSubtitle_IsSkippedNotThrown()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.SubsDbWithJunk");
        string subtitleDir = Path.Combine(hostDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);
        File.WriteAllText(Path.Combine(subtitleDir, "README.txt"), "not a subtitle");
        File.WriteAllText(Path.Combine(subtitleDir, "Movie.eng.full.ass"), "[Script Info]\n");

        List<Subtitle> result = InvokeGetSubtitles(BuildLocalStorage(), hostDir);

        result.Should().ContainSingle();
        result[0].Language.Should().Be("eng");
    }

    private static void WritePng(string path, int width, int height)
    {
        using Image<Rgba32> image = new(width, height);
        image.SaveAsPng(path);
    }
}
