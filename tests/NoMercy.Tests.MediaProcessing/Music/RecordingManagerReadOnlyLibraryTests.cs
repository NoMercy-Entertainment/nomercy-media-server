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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Music;
using NoMercy.MediaProcessing.Artists;
using NoMercy.MediaProcessing.Music;
using NoMercy.MediaProcessing.MusicGenres;
using NoMercy.MediaProcessing.Recordings;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// A library the server may only read (a Docker <c>:ro</c> volume, a read-only share)
/// still imports: a track that cannot be moved to its Picard path stays where it is.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RecordingManagerReadOnlyLibraryTests
{
    private const string LibraryRoot = "/synthetic/Music";
    private const string SourceKey = "Music/Loose/01 Track.mp3";

    public static TheoryData<Exception> ReadOnlyFailures =>
        new()
        {
            new IOException("Read-only file system : '/synthetic/Music/Loose/01 Track.mp3'"),
            new UnauthorizedAccessException("Access to the path is denied."),
        };

    private static (
        MusicBrainzReleaseAppends Release,
        MusicBrainzTrack Track,
        string Target
    ) Album()
    {
        MusicBrainzReleaseAppends release = new() { Title = "Synthetic Album" };
        MusicBrainzTrack track = new()
        {
            Id = Guid.Parse("00000000-0000-4000-8000-000000000001"),
            Title = "Synthetic Track",
            Position = 1,
        };
        MusicNamingContext context = MusicEncodeDispatcher.NamingContextFor(release, track);
        string target = PicardNaming.Sanitize(
            $"{PicardNaming.BuildDirectory(context)}/{PicardNaming.BuildFileName(context)}.mp3"
        );
        return (release, track, target);
    }

    private static RecordingManager ManagerOf(
        Mock<IStorage> storage,
        Mock<IRecordingRepository> recordings
    )
    {
        Mock<IStorageFactory> factory = new();
        factory
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storage.Object);
        return new(
            recordings.Object,
            Mock.Of<IMusicGenreRepository>(),
            Mock.Of<IArtistRepository>(),
            new LocalStorageDriver(),
            factory.Object,
            NullLogger<RecordingManager>.Instance
        );
    }

    private static Folder LibraryFolder() =>
        new()
        {
            Id = Ulid.NewUlid(),
            DriverId = Ulid.NewUlid(),
            Path = "Music",
        };

    private static MediaFile SourceFile(MusicBrainzTrack track) =>
        new()
        {
            Path = $"{LibraryRoot}/Loose/01 Track.mp3",
            FFprobe = new() { Format = new() { Tags = new() { ["title"] = track.Title } } },
        };

    [Theory]
    [MemberData(nameof(ReadOnlyFailures))]
    public async Task A_track_the_library_refuses_to_move_is_imported_where_it_is(Exception refusal)
    {
        (MusicBrainzReleaseAppends release, MusicBrainzTrack track, string target) = Album();

        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.ExistsAsync(SourceKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        storage
            .Setup(s => s.ExistsAsync($"Music/{target}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        storage
            .Setup(s => s.MoveAsync(SourceKey, $"Music/{target}", It.IsAny<CancellationToken>()))
            .ThrowsAsync(refusal);

        Mock<IRecordingRepository> recordings = new();
        RecordingManager manager = ManagerOf(storage, recordings);
        MediaFile mediaFile = SourceFile(track);

        Func<Task> relocate = () =>
            manager.RelocateToPicardLayout(release, track, mediaFile, LibraryFolder(), LibraryRoot);

        await relocate.Should().NotThrowAsync("a read-only library is read, never written");
        mediaFile.Path.Should().Be($"{LibraryRoot}/Loose/01 Track.mp3");
        recordings.Verify(
            repository =>
                repository.RepointTrackFile(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<TrackFilePlacement>()
                ),
            Times.Never,
            "the file did not move, so no row may point at the Picard path"
        );
    }

    [Theory]
    [MemberData(nameof(ReadOnlyFailures))]
    public async Task An_occupied_target_the_library_refuses_to_free_leaves_both_files_alone(
        Exception refusal
    )
    {
        (MusicBrainzReleaseAppends release, MusicBrainzTrack track, string target) = Album();

        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        storage
            .Setup(s =>
                s.MoveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(refusal);

        Mock<IRecordingRepository> recordings = new();
        RecordingManager manager = ManagerOf(storage, recordings);
        MediaFile mediaFile = SourceFile(track);

        Func<Task> relocate = () =>
            manager.RelocateToPicardLayout(release, track, mediaFile, LibraryFolder(), LibraryRoot);

        await relocate.Should().NotThrowAsync();
        mediaFile.Path.Should().Be($"{LibraryRoot}/Loose/01 Track.mp3");
        storage.Verify(
            s => s.MoveAsync(SourceKey, $"Music/{target}", It.IsAny<CancellationToken>()),
            Times.Never,
            "the occupant could not be moved aside, so the source must not be moved onto it"
        );
        recordings.Verify(
            repository =>
                repository.RepointTrackFile(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<TrackFilePlacement>()
                ),
            Times.Never
        );
    }
}
