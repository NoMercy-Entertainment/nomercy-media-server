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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.Encoder.Audio;
using NoMercy.Events;
using NoMercy.Events.DriveMonitor;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage;

namespace NoMercy.Tests.OpticalMedia.Rip;

/// <summary>
/// Testable subclass that reports whichever (Folder, Library) tuple the test
/// configures — including the null/null "target no longer exists" case that
/// the real DB-backed <see cref="DiscRipJob.FetchTargetsAsync"/> can also
/// return.
/// </summary>
file sealed class ConfigurableTargetsDiscRipJob(Folder? folder, Library? library) : DiscRipJob
{
    protected override Task<(Folder? Folder, Library? Library)> FetchTargetsAsync(
        Ulid folderId,
        Ulid libraryId,
        CancellationToken cancellationToken
    ) => Task.FromResult((folder, library));
}

/// <summary>
/// Covers the small remaining surfaces of <see cref="DiscRipJob"/> not
/// exercised by the auto-apply / CD-tagging / encode-dispatch test files:
/// the fixed queue name/priority, <see cref="DiscRipJob.InjectStorageServices"/>
/// wiring every field from a real <see cref="IServiceProvider"/>,
/// preset-id resolution against a malformed id, the "target folder/library no
/// longer exists" branch, and <c>ResolveHostPath</c>'s fallback when
/// <see cref="IStorage.GetFullPath"/> throws.
/// </summary>
[Collection("EventBusProvider")]
[Trait("Category", "Unit")]
public class DiscRipJobMiscTests
{
    [Fact]
    public void QueueName_IsImport()
    {
        DiscRipJob job = new();
        job.QueueName.Should().Be("import");
    }

    [Fact]
    public void Priority_IsFive()
    {
        DiscRipJob job = new();
        job.Priority.Should().Be(5);
    }

    [Fact]
    public void InjectStorageServices_ResolvesEveryDependencyFromServiceProvider()
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(Mock.Of<IDiscRipper>());
        services.AddSingleton(
            new DiscIdentificationService([], NullLogger<DiscIdentificationService>.Instance)
        );
        services.AddSingleton(Mock.Of<IStorageFactory>());
        services.AddSingleton(Mock.Of<IStorageDriver>());
        services.AddSingleton(new DriveLockRegistry());
        services.AddSingleton(Mock.Of<IAudioMetadataWriter>());
        using ServiceProvider provider = services.BuildServiceProvider();

        DiscRipJob job = new();
        job.InjectStorageServices(provider);

        job.LoggerFactory.Should().BeSameAs(NullLoggerFactory.Instance);
        job.DiscRipper.Should().NotBeNull();
        job.IdentificationService.Should().NotBeNull();
        job.StorageFactory.Should().NotBeNull();
        job.StorageDriver.Should().NotBeNull();
        job.DriveLockRegistry.Should().NotBeNull();
        job.AudioMetadataWriter.Should().NotBeNull();
        job.MusicBrainzReleaseClient.Should()
            .NotBeNull("constructed directly since it isn't DI-registered");
        // QueueRunner.Current is never started by this test process, so the
        // dispatcher falls back to null — this proves that fallback runs
        // rather than throwing when no queue runner is active.
        job.JobDispatcher.Should().BeNull();
    }

    [Fact]
    public async Task Handle_TargetsNoLongerExist_PublishesErrorAndReturns()
    {
        RipRequest request = new(
            DrivePath: "D:\\",
            SelectedTitleIndices: [1],
            MetadataId: null,
            Custom: new(Title: "Movie", Year: 2024, Type: MediaType.Movie, PosterUrl: null),
            LibraryId: Ulid.NewUlid(),
            FolderId: Ulid.NewUlid(),
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: [],
            Mode: RipMode.RipAndEncode,
            DiscType: OpticalDiscType.Dvd
        );

        Mock<IDiscRipper> ripperMock = new();
        ripperMock
            .Setup(r =>
                r.RipAsync(
                    It.IsAny<RipRequest>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([
                new DiscRipResult(1, "/tmp/x.mkv", true, TimeSpan.FromMinutes(10), 1000, null),
            ]);

        List<DriveStateChangedEvent> published = [];
        Mock<IEventBus> busMock = new();
        busMock
            .Setup(b =>
                b.PublishAsync(It.IsAny<DriveStateChangedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<DriveStateChangedEvent, CancellationToken>((evt, _) => published.Add(evt))
            .Returns(Task.CompletedTask);
        EventBusProvider.Configure(busMock.Object);

        ConfigurableTargetsDiscRipJob job = new(folder: null, library: null)
        {
            Request = request,
            OutputDir = Path.GetTempPath(),
            TargetFolderId = request.FolderId,
            TargetLibraryId = request.LibraryId,
            TargetLibraryType = "movie",
            DiscRipper = ripperMock.Object,
            IdentificationService = new([], NullLogger<DiscIdentificationService>.Instance),
            StorageFactory = Mock.Of<IStorageFactory>(),
            StorageDriver = Mock.Of<IStorageDriver>(),
            DriveLockRegistry = new(),
            LoggerFactory = NullLoggerFactory.Instance,
        };

        await job.Handle();

        published.Select(e => e.DriveStateData.Method).Should().Contain("rip_error");
        published
            .Select(e => e.DriveStateData.Message)
            .Should()
            .Contain(m => m.Contains("no longer exists"));
    }

    [Fact]
    public void PublishProgress_UnrecognizedStatus_FallsBackToRipProgressMethodName()
    {
        // "started"/"complete"/"error"/"pending" are the only statuses any
        // call site in DiscRipJob ever passes — the switch's `_ =>
        // "rip_progress"` default arm is otherwise unreachable from the
        // public API, so it's exercised directly via reflection.
        RipRequest request = new(
            DrivePath: "D:\\",
            SelectedTitleIndices: [1],
            MetadataId: null,
            Custom: null,
            LibraryId: Ulid.NewUlid(),
            FolderId: Ulid.NewUlid(),
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: []
        );
        DiscRipJob job = new(request, Path.GetTempPath(), null, null, null)
        {
            LoggerFactory = NullLoggerFactory.Instance,
        };

        List<DriveStateChangedEvent> published = [];
        Mock<IEventBus> busMock = new();
        busMock
            .Setup(b =>
                b.PublishAsync(It.IsAny<DriveStateChangedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<DriveStateChangedEvent, CancellationToken>((evt, _) => published.Add(evt))
            .Returns(Task.CompletedTask);
        EventBusProvider.Configure(busMock.Object);

        MethodInfo publishProgress = typeof(DiscRipJob).GetMethod(
            "PublishProgress",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;
        publishProgress.Invoke(job, ["unknown-status", "some message"]);

        published.Should().ContainSingle();
        published[0].DriveStateData.Method.Should().Be("rip_progress");
    }

    [Fact]
    public void ResolvePresetId_MalformedUlidString_ReturnsNull()
    {
        MethodInfo resolvePresetId = typeof(DiscRipJob).GetMethod(
            "ResolvePresetId",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;

        object? result = resolvePresetId.Invoke(
            null,
            ["not-a-valid-ulid", Array.Empty<NoMercy.Database.Models.Media.EncodingPresetFolder>()]
        );

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveHostPath_StorageThrows_FallsBackToRawSubPath()
    {
        Mock<IStorage> storageMock = new();
        storageMock.Setup(s => s.GetFullPath(It.IsAny<string>())).Throws<NotSupportedException>();

        MethodInfo resolveHostPath = typeof(DiscRipJob).GetMethod(
            "ResolveHostPath",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;

        object? result = resolveHostPath.Invoke(null, [storageMock.Object, "relative/path.mkv"]);

        result.Should().Be("relative/path.mkv");
    }
}
