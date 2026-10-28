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
using NoMercy.Database.Models.Music;
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
/// A host can store a moved file under a name with replaced characters. The track rows
/// have to address the stored name, or the next organize pass moves the file again.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RecordingManagerHostReplacedNameTests
{
    private const string LibraryRoot = "/synthetic/Music";

    [Fact]
    public async Task Track_rows_follow_a_file_the_host_stored_under_a_replaced_character()
    {
        MusicBrainzReleaseAppends release = new() { Title = "Synthetic Album" };
        MusicBrainzTrack track = new()
        {
            Id = Guid.NewGuid(),
            Title = "Synthetic ❤ Track",
            Position = 1,
        };
        MusicNamingContext context = MusicEncodeDispatcher.NamingContextFor(release, track);
        string target = PicardNaming.Sanitize(
            $"{PicardNaming.BuildDirectory(context)}/{PicardNaming.BuildFileName(context)}.mp3"
        );
        string storedTarget = target.Replace('❤', '?');

        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.ExistsAsync("Music/Loose/01 Track.mp3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        storage
            .Setup(s => s.ExistsAsync($"Music/{target}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        storage
            .Setup(s =>
                s.MoveAsync(
                    "Music/Loose/01 Track.mp3",
                    $"Music/{target}",
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);
        storage
            .Setup(s =>
                s.ListAsync(
                    StoragePathHelpers.GetParent($"Music/{target}")!,
                    null,
                    false,
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                new[]
                {
                    new StorageEntry($"Music/{storedTarget}", false, 1, DateTimeOffset.UnixEpoch),
                }.ToAsyncEnumerable()
            );

        TrackFilePlacement? repointed = null;
        Mock<IRecordingRepository> recordings = new();
        recordings
            .Setup(repository =>
                repository.RepointTrackFile(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<TrackFilePlacement>()
                )
            )
            .Callback<string, string, TrackFilePlacement>((_, _, to) => repointed = to)
            .ReturnsAsync(1);

        Mock<IStorageFactory> factory = new();
        factory
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(storage.Object);
        RecordingManager manager = new(
            recordings.Object,
            Mock.Of<IMusicGenreRepository>(),
            Mock.Of<IArtistRepository>(),
            new LocalStorageDriver(),
            factory.Object,
            NullLogger<RecordingManager>.Instance
        );

        Folder libraryFolder = new()
        {
            Id = Ulid.NewUlid(),
            DriverId = Ulid.NewUlid(),
            Path = "Music",
        };
        MediaFile mediaFile = new()
        {
            Path = $"{LibraryRoot}/Loose/01 Track.mp3",
            FFprobe = new() { Format = new() { Tags = new() { ["title"] = track.Title } } },
        };

        await manager.RelocateToPicardLayout(release, track, mediaFile, libraryFolder, LibraryRoot);

        mediaFile.Path.Should().Be($"{LibraryRoot}/{storedTarget}");
        repointed.Should().NotBeNull();
        repointed!.Filename.Should().Be("/" + StoragePathHelpers.GetName(storedTarget));
    }
}
