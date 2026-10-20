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
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Analysis;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

// ---------------------------------------------------------------------------
// VideoEncodeJob's post-encode registration retries FindFiles on a bounded
// backoff exactly when it comes back with 0 registered candidates — see
// VideoEncodeJob.ScanEncodedOutputWithRetryAsync. That decision hinges entirely
// on FindFiles' bool return value, so this pins the contract: an empty scan
// (here, a library whose target movie/show cannot be resolved so no folder is
// ever probed) must return false, not throw and not silently report success.
//
// The info-page invalidation tests below live in the same class because they
// exercise the same "Publish refresh events only after successful commit"
// switch at the tail of FindFiles. That switch runs unconditionally on
// library.Type regardless of whether the scan found any files, so a
// Movie/Tv/Anime whose Folder is null (Paths() returns immediately, no DB or
// storage access) is sufficient to reach it without standing up a real scan.
// ---------------------------------------------------------------------------
[Trait("Category", "Unit")]
[Collection("EventBusProvider")]
public sealed class FileManagerFindFilesTests
{
    private static IEventBus? GetCurrentInstance() =>
        (IEventBus?)
            typeof(EventBusProvider)
                .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null);

    private static void SetInstance(IEventBus? bus) =>
        typeof(EventBusProvider)
            .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, bus);

    private static (
        Mock<IEventBus> BusMock,
        List<LibraryRefreshedEvent> Captured
    ) BuildCapturingBus()
    {
        List<LibraryRefreshedEvent> captured = [];
        Mock<IEventBus> busMock = new();
        busMock
            .Setup(bus =>
                bus.PublishAsync(It.IsAny<LibraryRefreshedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<LibraryRefreshedEvent, CancellationToken>((evt, _) => captured.Add(evt))
            .Returns(Task.CompletedTask);
        return (busMock, captured);
    }

    private static FileManager BuildManager(Mock<IFileRepository> repoMock)
    {
        Mock<IStorageFactory> factoryMock = new();
        Mock<IMediaAnalyzer> mediaAnalyzerMock = new();
        return new(
            repoMock.Object,
            factoryMock.Object,
            mediaAnalyzerMock.Object,
            TestFilenameParser.Default
        );
    }

    private static bool HasKey(LibraryRefreshedEvent evt, params object?[] key) =>
        evt.QueryKey.SequenceEqual(key);

    [Fact]
    public async Task FindFiles_NoResolvableMediaFolder_ReturnsFalse()
    {
        Mock<IFileRepository> repoMock = new();
        repoMock
            .Setup(repository => repository.MediaType(It.IsAny<int>(), It.IsAny<Library>()))
            .ReturnsAsync(((Movie?)null, (Tv?)null, MediaTypes.MovieMediaType));

        FileManager manager = BuildManager(repoMock);

        Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.MovieMediaType };

        // No Movie is resolvable for this id, so FileManager.Paths() has no
        // folder to look under and the scan never finds a candidate file.
        bool hasCandidates = await manager.FindFiles(id: 999_999, library);

        hasCandidates.Should().BeFalse();
    }

    [Fact]
    public async Task FindFiles_MovieScan_PublishesInfoPageEvent_WithStringId()
    {
        IEventBus? previous = GetCurrentInstance();
        try
        {
            (Mock<IEventBus> busMock, List<LibraryRefreshedEvent> captured) = BuildCapturingBus();
            EventBusProvider.Configure(busMock.Object);

            Movie movie = new() { Id = 550, Folder = null };
            Mock<IFileRepository> repoMock = new();
            repoMock
                .Setup(repository => repository.MediaType(It.IsAny<int>(), It.IsAny<Library>()))
                .ReturnsAsync((movie, (Tv?)null, MediaTypes.MovieMediaType));

            FileManager manager = BuildManager(repoMock);
            Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.MovieMediaType };

            await manager.FindFiles(id: 42, library);

            captured
                .Should()
                .ContainSingle(
                    evt => HasKey(evt, "libraries", library.Id.ToString()),
                    "the existing grid invalidation must not regress"
                );

            captured
                .Should()
                .ContainSingle(
                    evt => HasKey(evt, "movie", "42"),
                    "the movie info page must be invalidated using the raw scan id, as a string"
                );

            captured
                .SelectMany(evt => evt.QueryKey)
                .Should()
                .OnlyContain(
                    element => element is string,
                    "every QueryKey element must be a string, never a raw int"
                );
        }
        finally
        {
            SetInstance(previous);
        }
    }

    [Fact]
    public async Task FindFiles_TvScan_PublishesInfoPageEvent_UsingShowId()
    {
        IEventBus? previous = GetCurrentInstance();
        try
        {
            (Mock<IEventBus> busMock, List<LibraryRefreshedEvent> captured) = BuildCapturingBus();
            EventBusProvider.Configure(busMock.Object);

            Tv show = new() { Id = 1399, Folder = null };
            Mock<IFileRepository> repoMock = new();
            repoMock
                .Setup(repository => repository.MediaType(It.IsAny<int>(), It.IsAny<Library>()))
                .ReturnsAsync(((Movie?)null, show, MediaTypes.TvMediaType));

            FileManager manager = BuildManager(repoMock);
            Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.TvMediaType };

            // The scan id (42) intentionally differs from Show.Id (1399) so the
            // assertion proves the published key uses Show.Id, mirroring the
            // existing `Show?.Id ?? id` usage elsewhere in this method.
            await manager.FindFiles(id: 42, library);

            captured
                .Should()
                .ContainSingle(
                    evt => HasKey(evt, "libraries", library.Id.ToString()),
                    "the existing grid invalidation must not regress"
                );

            captured
                .Should()
                .ContainSingle(
                    evt => HasKey(evt, "tv", "1399"),
                    "the tv info page must be invalidated using Show.Id, not the raw scan id"
                );

            captured
                .SelectMany(evt => evt.QueryKey)
                .Should()
                .OnlyContain(
                    element => element is string,
                    "every QueryKey element must be a string, never a raw int"
                );
        }
        finally
        {
            SetInstance(previous);
        }
    }

    [Fact]
    public async Task FindFiles_AnimeScan_PublishesTvKey_NotAnimeKey()
    {
        IEventBus? previous = GetCurrentInstance();
        try
        {
            (Mock<IEventBus> busMock, List<LibraryRefreshedEvent> captured) = BuildCapturingBus();
            EventBusProvider.Configure(busMock.Object);

            Tv show = new() { Id = 888, Folder = null };
            Mock<IFileRepository> repoMock = new();
            repoMock
                .Setup(repository => repository.MediaType(It.IsAny<int>(), It.IsAny<Library>()))
                .ReturnsAsync(((Movie?)null, show, MediaTypes.AnimeMediaType));

            FileManager manager = BuildManager(repoMock);
            Library library = new() { Id = Ulid.NewUlid(), Type = MediaTypes.AnimeMediaType };

            await manager.FindFiles(id: 42, library);

            captured
                .Should()
                .ContainSingle(
                    evt => HasKey(evt, "tv", "888"),
                    "an anime library must publish the 'tv' info-page key — the web client has no /anime/:id route"
                );

            captured
                .Should()
                .NotContain(
                    evt => evt.QueryKey.Length > 0 && Equals(evt.QueryKey[0], "anime"),
                    "the info-page key must never be 'anime'"
                );
        }
        finally
        {
            SetInstance(previous);
        }
    }
}
