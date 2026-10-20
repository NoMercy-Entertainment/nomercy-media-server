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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files;

// ---------------------------------------------------------------------------
// GitHub issue #38: a post-encode scan (VideoEncodeJob.ScanEncodedOutputWithRetryAsync)
// already knows which episode it dispatched the encode for. Before this fix,
// StoreVideoItem always re-derived the episode from the output filename via
// IFileRepository.GetEpisode — for a title whose name itself contains digits
// that read as a season/episode (South Park's "1%" parsing as S00E12 instead
// of the real S15E12), that re-derivation lands on the wrong episode and the
// encode silently overwrites the wrong row.
//
// FileManager.HintDispatchedMediaId + FileManager.Storage.StoreVideoItem's
// isDispatchedTarget branch fix this by trusting the known id for the single
// file FilterFiles narrowed the scan to. These tests drive StoreVideoItem
// (private) via reflection against a real LocalStorage over an empty temp
// fixture tree — mirroring FileManagerMakeMetadataTests' pattern — with a
// mocked IFileRepository so GetEpisode and GetEpisodeById can be told to
// disagree, proving which one the stored VideoFile actually used.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
public sealed class FileManagerStoreVideoItemDispatchedEpisodeTests : IDisposable
{
    private const int ShowId = 900;
    private const int CorrectEpisodeId = 555; // South Park S15E12, the real dispatched episode.
    private const int FilenameDerivedEpisodeId = 999; // What GetEpisode(filename) would wrongly return (S00E12).

    private readonly string _tempRoot;

    public FileManagerStoreVideoItemDispatchedEpisodeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-storevideoitem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static IStorage BuildLocalStorage()
    {
        LocalStorageDriver driver = new();
        return new LocalStorage(driver, new StoragePathGuard([], driver));
    }

    private static void SetPrivateProperty(FileManager manager, string name, object? value)
    {
        PropertyInfo property =
            typeof(FileManager).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{name} not found");
        property.SetValue(manager, value);
    }

    private static async Task InvokeStoreVideoItem(FileManager manager, MediaFile item)
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "StoreVideoItem",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("StoreVideoItem not found");

        await (Task)method.Invoke(manager, [item])!;
    }

    /// <summary>
    /// Builds a FileManager wired to a mocked IFileRepository (so GetEpisode and
    /// GetEpisodeById can be scripted to disagree) and a real LocalStorage over
    /// a fresh empty fixture directory (so StoreVideoItem's storage calls —
    /// GetSubtitles/GetExtraFiles/MakeMetadata's hash lists — resolve against a
    /// real, if empty, folder instead of throwing on a missing mock).
    /// </summary>
    private (
        FileManager Manager,
        Mock<IFileRepository> RepoMock,
        string HostDir,
        MediaFile Item,
        Folder Folder
    ) BuildScenario()
    {
        string hostDir = Path.Combine(_tempRoot, "South.Park.(1997)", "Season.15");
        Directory.CreateDirectory(hostDir);

        Folder folder = new()
        {
            Id = Ulid.NewUlid(),
            Path = _tempRoot,
            DriverId = Ulid.NewUlid(),
        };

        // The real issue's shape: the release title itself ("1%") contains a
        // digit a naive season/episode parser reads as an episode number.
        const string fileName = "South.Park.1.Percent.S15E12.NoMercy.mkv";
        MediaFile item = new() { Path = Path.Combine(hostDir, fileName) };

        Mock<IFileRepository> repoMock = new();
        repoMock
            .Setup(repo => repo.GetEpisodeById(CorrectEpisodeId))
            .ReturnsAsync(
                new Episode
                {
                    Id = CorrectEpisodeId,
                    TvId = ShowId,
                    SeasonNumber = 15,
                    EpisodeNumber = 12,
                }
            );
        repoMock
            .Setup(repo => repo.GetEpisode(ShowId, It.IsAny<MediaFile>()))
            .ReturnsAsync(
                new Episode
                {
                    Id = FilenameDerivedEpisodeId,
                    TvId = ShowId,
                    SeasonNumber = 0,
                    EpisodeNumber = 12,
                }
            );
        repoMock
            .Setup(repo => repo.StoreMetadata(It.IsAny<Metadata>()))
            .ReturnsAsync(Ulid.NewUlid());
        repoMock
            .Setup(repo => repo.StoreVideoFile(It.IsAny<VideoFile>()))
            .Returns(Task.CompletedTask);

        Mock<IStorageFactory> factoryMock = new();
        factoryMock
            .Setup(factory => factory.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(BuildLocalStorage());
        Mock<IMediaAnalyzer> mediaAnalyzerMock = new();

        FileManager manager = new(
            repoMock.Object,
            factoryMock.Object,
            mediaAnalyzerMock.Object,
            TestFilenameParser.Default
        );

        SetPrivateProperty(manager, "Show", new Tv { Id = ShowId, Title = "South Park" });
        SetPrivateProperty(manager, "Folders", new List<Folder> { folder });

        return (manager, repoMock, hostDir, item, folder);
    }

    [Fact]
    public async Task StoreVideoItem_WithDispatchedHintAndMatchingFilter_UsesTheDispatchedEpisode_NotTheFilenameDerivedOne()
    {
        (FileManager manager, Mock<IFileRepository> repoMock, _, MediaFile item, _) =
            BuildScenario();

        // Mirrors VideoEncodeJob.ScanEncodedOutputWithRetryAsync: FilterFiles narrows
        // the scan to this one output file, HintDispatchedMediaId carries the episode
        // id it was dispatched for.
        manager.FilterFiles(Path.GetFileName(item.Path));
        manager.HintDispatchedMediaId(CorrectEpisodeId);

        VideoFile? stored = null;
        repoMock
            .Setup(repo => repo.StoreVideoFile(It.IsAny<VideoFile>()))
            .Callback<VideoFile>(vf => stored = vf)
            .Returns(Task.CompletedTask);

        await InvokeStoreVideoItem(manager, item);

        stored.Should().NotBeNull();
        stored!
            .EpisodeId.Should()
            .Be(
                CorrectEpisodeId,
                "the hinted episode id must win over whatever the filename parser would have produced"
            );
        stored.EpisodeId.Should().NotBe(FilenameDerivedEpisodeId);

        repoMock.Verify(
            repo => repo.GetEpisodeById(CorrectEpisodeId),
            Times.Once,
            "the dispatched-target branch must resolve the episode by id"
        );
        repoMock.Verify(
            repo => repo.GetEpisode(It.IsAny<int?>(), It.IsAny<MediaFile>()),
            Times.Never,
            "the filename-derived lookup must not run once the dispatched hint applies"
        );
    }

    /// <summary>
    /// Regression guard on the general path: every other caller (initial import,
    /// manual rescan) never calls HintDispatchedMediaId, so DispatchedMediaId stays
    /// null and StoreVideoItem must keep resolving by filename exactly as before.
    /// </summary>
    [Fact]
    public async Task StoreVideoItem_WithoutDispatchedHint_StillResolvesByFilename()
    {
        (FileManager manager, Mock<IFileRepository> repoMock, _, MediaFile item, _) =
            BuildScenario();

        // No FilterFiles / HintDispatchedMediaId call — the ordinary rescan path.

        VideoFile? stored = null;
        repoMock
            .Setup(repo => repo.StoreVideoFile(It.IsAny<VideoFile>()))
            .Callback<VideoFile>(vf => stored = vf)
            .Returns(Task.CompletedTask);

        await InvokeStoreVideoItem(manager, item);

        stored.Should().NotBeNull();
        stored!
            .EpisodeId.Should()
            .Be(
                FilenameDerivedEpisodeId,
                "with no dispatched hint, the filename-derived lookup must still be used"
            );

        repoMock.Verify(
            repo => repo.GetEpisode(ShowId, It.IsAny<MediaFile>()),
            Times.Once,
            "the general (non-dispatched) path must still call the filename-derived lookup"
        );
        repoMock.Verify(
            repo => repo.GetEpisodeById(It.IsAny<int>()),
            Times.Never,
            "GetEpisodeById must never run when no episode id was hinted"
        );
    }
}
