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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Onboarding;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.OpticalMedia.Onboarding;

[Trait("Category", "Unit")]
public class DiscOnboardingOrchestratorConfirmTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _dbOptions;

    public DiscOnboardingOrchestratorConfirmTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext seedContext = new(_dbOptions);
        seedContext.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<MediaContext> ContextFactory => new TestDbContextFactory(_dbOptions);

    private Ulid SeedLibrary(string type = "movie")
    {
        Ulid libraryId = Ulid.NewUlid();
        using MediaContext seedContext = new(_dbOptions);
        seedContext.Libraries.Add(
            new Library
            {
                Id = libraryId,
                Title = "Movies",
                Type = type,
                Order = 1,
            }
        );
        seedContext.SaveChanges();
        return libraryId;
    }

    private static Mock<IDriveMonitor> DriveMonitorFor(string drivePath, OpticalDiscType discType)
    {
        Mock<IDriveMonitor> mock = new();
        mock.Setup(m => m.GetDrives())
            .Returns([new DiscDrive(drivePath, "Test Drive", true, discType)]);
        return mock;
    }

    private static DiscSourceFactory SourceFactoryFor(OpticalDiscType type, DiscInfo info)
    {
        Mock<IDiscSource> source = new();
        source.SetupGet(s => s.Type).Returns(type);
        source
            .Setup(s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        return new DiscSourceFactory([source.Object]);
    }

    [Fact]
    public async Task ConfirmAsync_DispatchesRipJobWithChosenCandidateAsCustomMetadata_AndTransitionsToRipping()
    {
        Ulid libraryId = SeedLibrary("movie");
        Ulid folderId = Ulid.NewUlid();

        DiscOnboardingSessionStore store = new();
        store.Set(
            DiscOnboardingSession
                .Create("D:\\")
                .WithCandidates(
                    [new("tmdb", "27205", "Inception", 2010, null, null, 0.6)],
                    DiscOnboardingState.AwaitingConfirm
                )
        );

        Mock<IJobDispatcher> dispatcher = new();
        RipRequest? dispatchedRequest = null;
        string? dispatchedTargetLibraryType = null;
        dispatcher
            .Setup(d => d.Dispatch(It.IsAny<DiscRipJob>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<object, string, int>(
                (job, _, _) =>
                {
                    DiscRipJob discRipJob = (DiscRipJob)job;
                    dispatchedRequest = discRipJob.Request;
                    dispatchedTargetLibraryType = discRipJob.TargetLibraryType;
                }
            );

        DiscInfo probeInfo = new(OpticalDiscType.Dvd, "INCEPTION", [], null, TimeSpan.FromHours(2));

        DiscOnboardingOrchestrator orchestrator = new(
            SourceFactoryFor(OpticalDiscType.Dvd, probeInfo),
            identificationService: null!,
            store,
            Mock.Of<IEventBus>(),
            dispatcher.Object,
            DriveMonitorFor("D:\\", OpticalDiscType.Dvd).Object,
            ContextFactory
        );

        DiscCandidate chosen = new(
            "tmdb",
            "27205",
            "Inception",
            2010,
            null,
            null,
            0.6,
            Type: MediaType.Movie
        );

        DiscOnboardingSession result = await orchestrator.ConfirmAsync(
            "D:\\",
            chosen,
            [1],
            libraryId,
            folderId,
            CancellationToken.None
        );

        result.State.Should().Be(DiscOnboardingState.Ripping);
        result.JobId.Should().NotBeNullOrEmpty();
        result.LibraryId.Should().Be(libraryId);
        result.ConfirmedTmdbId.Should().Be(27205);
        result.ConfirmedMediaType.Should().Be("movie");
        dispatchedRequest.Should().NotBeNull();
        dispatchedRequest!.Custom.Should().NotBeNull();
        dispatchedRequest.Custom!.Title.Should().Be("Inception");
        dispatchedRequest.LibraryId.Should().Be(libraryId);
        dispatchedRequest.FolderId.Should().Be(folderId);
        dispatchedRequest.DiscType.Should().Be(OpticalDiscType.Dvd);
        dispatchedTargetLibraryType.Should().Be("movie");
    }

    [Fact]
    public async Task ConfirmAsync_CdWithNoSelectedTitles_EnrichesWithAllProbedTracks()
    {
        Ulid libraryId = SeedLibrary("music");
        Ulid folderId = Ulid.NewUlid();

        DiscOnboardingSessionStore store = new();
        store.Set(
            DiscOnboardingSession
                .Create("E:\\")
                .WithCandidates(
                    [new("musicbrainz", "abc", "Some Album", 2020, null, null, 0.9)],
                    DiscOnboardingState.AwaitingConfirm
                )
        );

        Mock<IJobDispatcher> dispatcher = new();
        RipRequest? dispatchedRequest = null;
        dispatcher
            .Setup(d => d.Dispatch(It.IsAny<DiscRipJob>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<object, string, int>(
                (job, _, _) => dispatchedRequest = ((DiscRipJob)job).Request
            );

        DiscInfo probeInfo = new(
            OpticalDiscType.Cd,
            "ALBUM",
            [],
            [
                new DiscTrack(1, "Track 1", "Artist", TimeSpan.FromMinutes(3), 44100, 2),
                new DiscTrack(2, "Track 2", "Artist", TimeSpan.FromMinutes(4), 44100, 2),
            ],
            TimeSpan.FromMinutes(7)
        );

        DiscOnboardingOrchestrator orchestrator = new(
            SourceFactoryFor(OpticalDiscType.Cd, probeInfo),
            identificationService: null!,
            store,
            Mock.Of<IEventBus>(),
            dispatcher.Object,
            DriveMonitorFor("E:\\", OpticalDiscType.Cd).Object,
            ContextFactory
        );

        DiscCandidate chosen = new("musicbrainz", "abc", "Some Album", 2020, null, null, 0.9);

        await orchestrator.ConfirmAsync(
            "E:\\",
            chosen,
            [],
            libraryId,
            folderId,
            CancellationToken.None
        );

        dispatchedRequest.Should().NotBeNull();
        dispatchedRequest!.DiscType.Should().Be(OpticalDiscType.Cd);
        dispatchedRequest.SelectedTitleIndices.Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task ConfirmAsync_ProtectedDisc_ThrowsAndDoesNotDispatch()
    {
        Ulid libraryId = SeedLibrary();
        Ulid folderId = Ulid.NewUlid();

        DiscOnboardingSessionStore store = new();
        store.Set(
            DiscOnboardingSession
                .Create("F:\\")
                .WithCandidates(
                    [new("tmdb", "1", "X", null, null, null, 1.0)],
                    DiscOnboardingState.AwaitingConfirm
                )
        );

        Mock<IJobDispatcher> dispatcher = new();
        DiscInfo protectedInfo = new(
            OpticalDiscType.BluRay,
            "PROTECTED",
            [],
            null,
            TimeSpan.Zero,
            new DiscProtection("AACS", null, "Unsupported disc key")
        );

        DiscOnboardingOrchestrator orchestrator = new(
            SourceFactoryFor(OpticalDiscType.BluRay, protectedInfo),
            identificationService: null!,
            store,
            Mock.Of<IEventBus>(),
            dispatcher.Object,
            DriveMonitorFor("F:\\", OpticalDiscType.BluRay).Object,
            ContextFactory
        );

        Func<Task> act = () =>
            orchestrator.ConfirmAsync(
                "F:\\",
                new("tmdb", "1", "X", null, null, null, 1.0),
                [1],
                libraryId,
                folderId,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
        dispatcher.Verify(
            d => d.Dispatch(It.IsAny<DiscRipJob>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ConfirmAsync_SessionAlreadyRipping_ThrowsAndDoesNotDispatchTwice()
    {
        Ulid libraryId = SeedLibrary();
        Ulid folderId = Ulid.NewUlid();

        DiscOnboardingSessionStore store = new();
        DiscOnboardingSession ripping = DiscOnboardingSession
            .Create("G:\\")
            .WithCandidates(
                [new("tmdb", "1", "X", null, null, null, 1.0)],
                DiscOnboardingState.AwaitingConfirm
            )
            .WithJob("existing-job-id");
        store.Set(ripping);

        Mock<IJobDispatcher> dispatcher = new();

        DiscOnboardingOrchestrator orchestrator = new(
            discSourceFactory: null!,
            identificationService: null!,
            store,
            Mock.Of<IEventBus>(),
            dispatcher.Object,
            Mock.Of<IDriveMonitor>(),
            ContextFactory
        );

        Func<Task> act = () =>
            orchestrator.ConfirmAsync(
                "G:\\",
                new("tmdb", "1", "X", null, null, null, 1.0),
                [1],
                libraryId,
                folderId,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
        dispatcher.Verify(
            d => d.Dispatch(It.IsAny<DiscRipJob>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ConfirmAsync_UnknownDrive_ThrowsInvalidOperationException()
    {
        DiscOnboardingOrchestrator orchestrator = new(
            discSourceFactory: null!,
            identificationService: null!,
            new DiscOnboardingSessionStore(),
            Mock.Of<IEventBus>(),
            Mock.Of<IJobDispatcher>(),
            Mock.Of<IDriveMonitor>(),
            ContextFactory
        );

        Func<Task> act = () =>
            orchestrator.ConfirmAsync(
                "Z:\\",
                new("tmdb", "1", "X", null, null, null, 1.0),
                [1],
                Ulid.NewUlid(),
                Ulid.NewUlid(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class TestDbContextFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => new(options);
    }
}
