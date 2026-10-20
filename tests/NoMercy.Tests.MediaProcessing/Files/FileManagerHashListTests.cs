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
// Regression tests for GetVideoHashList / GetAudioHashList path handling.
//
// Bug: when LocalStorage.List() returns scope-relative paths (Enforced=true,
// i.e. after the MakeDriverIdRequiredAndSeedLocalDrivers migration) the old
// code did Path.GetRelativePath(absoluteHostFolder, scopeRelativePath) which
// produced "../../../…" garbage.  The fix replaces that with a composition of
// storage.GetName(dir.Path) + "/" + storage.GetName(playlistPath).
//
// Both modes must produce exactly "/" + variantDir + "/" + playlistFile.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerHashListTests : IDisposable
{
    // -----------------------------------------------------------------------
    // Private-method helpers via reflection
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

    private static List<IVideo> InvokeGetVideoHashList(
        FileManager manager,
        IStorage storage,
        string hostFolder
    )
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "GetVideoHashList",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("GetVideoHashList not found");

        return (List<IVideo>)method.Invoke(manager, [storage, hostFolder])!;
    }

    private static List<IAudio> InvokeGetAudioHashList(
        FileManager manager,
        IStorage storage,
        string hostFolder
    )
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "GetAudioHashList",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("GetAudioHashList not found");

        return (List<IAudio>)method.Invoke(manager, [storage, hostFolder])!;
    }

    // -----------------------------------------------------------------------
    // Temp directory scaffolding
    // -----------------------------------------------------------------------

    private readonly string _tempRoot;

    public FileManagerHashListTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    /// <summary>
    /// Builds the on-disk layout under <paramref name="hostDir"/>:
    ///   hostDir/video_1920x1080_SDR/video_1920x1080_SDR.m3u8
    ///   hostDir/audio_eng_aac/audio_eng_aac.m3u8
    /// </summary>
    private static void CreateScannerLayout(string hostDir)
    {
        string videoDir = Path.Combine(hostDir, "video_1920x1080_SDR");
        Directory.CreateDirectory(videoDir);
        File.WriteAllText(Path.Combine(videoDir, "video_1920x1080_SDR.m3u8"), "#EXTM3U");
        File.WriteAllText(Path.Combine(videoDir, "segment0.ts"), "data");

        string audioDir = Path.Combine(hostDir, "audio_eng_aac");
        Directory.CreateDirectory(audioDir);
        File.WriteAllText(Path.Combine(audioDir, "audio_eng_aac.m3u8"), "#EXTM3U");
    }

    // -----------------------------------------------------------------------
    // Enforced=false  (fresh install — guard has no root, List returns abs paths)
    // -----------------------------------------------------------------------

    [Fact]
    public void GetVideoHashList_EnforcedFalse_ReturnsCorrectHostRelativeFileName()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.(2020).NoMercy");
        CreateScannerLayout(hostDir);

        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([], driver);
        LocalStorage storage = new(driver, guard);

        FileManager manager = BuildFileManager();

        List<IVideo> results = InvokeGetVideoHashList(manager, storage, hostDir);

        results.Should().HaveCount(1);
        results[0].FileName.Should().Be("/video_1920x1080_SDR/video_1920x1080_SDR.m3u8");
        results[0].FileName.Should().NotContain("..");
    }

    [Fact]
    public void GetAudioHashList_EnforcedFalse_ReturnsCorrectHostRelativeFileName()
    {
        string hostDir = Path.Combine(_tempRoot, "Movie.(2020).NoMercy");
        CreateScannerLayout(hostDir);

        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([], driver);
        LocalStorage storage = new(driver, guard);

        FileManager manager = BuildFileManager();

        List<IAudio> results = InvokeGetAudioHashList(manager, storage, hostDir);

        results.Should().HaveCount(1);
        results[0].FileName.Should().Be("/audio_eng_aac/audio_eng_aac.m3u8");
        results[0].FileName.Should().NotContain("..");
    }

    // -----------------------------------------------------------------------
    // Enforced=true  (post-migration — guard has root, List returns scope-relative paths)
    // -----------------------------------------------------------------------

    [Fact]
    public void GetVideoHashList_EnforcedTrue_ReturnsCorrectHostRelativeFileName()
    {
        string root = _tempRoot;
        string hostDir = Path.Combine(root, "Movie.(2020).NoMercy");
        CreateScannerLayout(hostDir);

        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([root], driver);
        LocalStorage storage = new(driver, guard);

        // hostFolder is still the absolute OS path — this is how callers supply it.
        // storage.List() will return scope-relative paths under Enforced=true.
        FileManager manager = BuildFileManager();

        List<IVideo> results = InvokeGetVideoHashList(manager, storage, hostDir);

        results.Should().HaveCount(1);
        results[0].FileName.Should().Be("/video_1920x1080_SDR/video_1920x1080_SDR.m3u8");
        results[0].FileName.Should().NotContain("..");
    }

    [Fact]
    public void GetAudioHashList_EnforcedTrue_ReturnsCorrectHostRelativeFileName()
    {
        string root = _tempRoot;
        string hostDir = Path.Combine(root, "Movie.(2020).NoMercy");
        CreateScannerLayout(hostDir);

        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([root], driver);
        LocalStorage storage = new(driver, guard);

        FileManager manager = BuildFileManager();

        List<IAudio> results = InvokeGetAudioHashList(manager, storage, hostDir);

        results.Should().HaveCount(1);
        results[0].FileName.Should().Be("/audio_eng_aac/audio_eng_aac.m3u8");
        results[0].FileName.Should().NotContain("..");
    }

    // -----------------------------------------------------------------------
    // Both modes produce the SAME value — no behavioral divergence
    // -----------------------------------------------------------------------

    [Fact]
    public void GetVideoHashList_BothModes_ProduceSameFileName()
    {
        string root = _tempRoot;
        string hostDir = Path.Combine(root, "Movie.(2020).NoMercy");
        CreateScannerLayout(hostDir);

        LocalStorageDriver driver = new();

        LocalStorage openStorage = new(driver, new([], driver));
        LocalStorage enforcedStorage = new(driver, new([root], driver));

        FileManager manager = BuildFileManager();

        List<IVideo> openResults = InvokeGetVideoHashList(manager, openStorage, hostDir);
        List<IVideo> enforcedResults = InvokeGetVideoHashList(manager, enforcedStorage, hostDir);

        openResults.Should().HaveCount(1);
        enforcedResults.Should().HaveCount(1);
        openResults[0].FileName.Should().Be(enforcedResults[0].FileName);
    }

    [Fact]
    public void GetAudioHashList_BothModes_ProduceSameFileName()
    {
        string root = _tempRoot;
        string hostDir = Path.Combine(root, "Movie.(2020).NoMercy");
        CreateScannerLayout(hostDir);

        LocalStorageDriver driver = new();

        LocalStorage openStorage = new(driver, new([], driver));
        LocalStorage enforcedStorage = new(driver, new([root], driver));

        FileManager manager = BuildFileManager();

        List<IAudio> openResults = InvokeGetAudioHashList(manager, openStorage, hostDir);
        List<IAudio> enforcedResults = InvokeGetAudioHashList(manager, enforcedStorage, hostDir);

        openResults.Should().HaveCount(1);
        enforcedResults.Should().HaveCount(1);
        openResults[0].FileName.Should().Be(enforcedResults[0].FileName);
    }
}
