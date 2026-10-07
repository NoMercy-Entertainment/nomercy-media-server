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
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Music;

/// <summary>
/// Issue #500: a manual import of an album that sits OUTSIDE the destination library
/// (a sibling such as "Music Unsorted", or an unrelated "Incoming" folder) threw
/// FileNotFoundException on a path rebuilt from the library folder, so the album never
/// imported. These tests run the real LocalStorage and StoragePathGuard on a temp tree
/// with synthetic files.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RecordingManagerRelocateToPicardLayoutTests : IDisposable
{
    private readonly string _driveRoot;
    private readonly string _outsideRoot;
    private readonly IStorage _storage;
    private readonly RecordingManager _manager;
    private readonly Folder _libraryFolder;
    private readonly string _libraryRoot;

    public RecordingManagerRelocateToPicardLayoutTests()
    {
        string unique = Guid.NewGuid().ToString("N");
        _driveRoot = Path.Combine(Path.GetTempPath(), $"nm-picard-{unique}");
        _outsideRoot = Path.Combine(Path.GetTempPath(), $"nm-picard-outside-{unique}");
        Directory.CreateDirectory(Path.Combine(_driveRoot, "Music"));
        Directory.CreateDirectory(_outsideRoot);

        LocalStorageDriver driver = new();
        _storage = new LocalStorage(driver, new StoragePathGuard([_driveRoot], driver));

        _libraryFolder = new()
        {
            Id = Ulid.NewUlid(),
            DriverId = Ulid.NewUlid(),
            Path = "Music",
        };
        _libraryRoot = _storage.GetFullPath("Music").Replace('\\', '/');

        Mock<IStorageFactory> factory = new();
        factory
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(_storage);

        _manager = new(
            Mock.Of<IRecordingRepository>(),
            Mock.Of<IMusicGenreRepository>(),
            Mock.Of<IArtistRepository>(),
            driver,
            factory.Object,
            NullLogger<RecordingManager>.Instance
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(_driveRoot))
            Directory.Delete(_driveRoot, recursive: true);
        if (Directory.Exists(_outsideRoot))
            Directory.Delete(_outsideRoot, recursive: true);
    }

    private static MusicBrainzReleaseAppends Release() => new() { Title = "Synthetic Album" };

    private static MusicBrainzTrack Track() =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = "Synthetic Track",
            Position = 1,
        };

    private static string TargetRelativePath(
        MusicBrainzReleaseAppends release,
        MusicBrainzTrack track
    )
    {
        MusicNamingContext context = MusicEncodeDispatcher.NamingContextFor(release, track);
        return PicardNaming.Sanitize(
            $"{PicardNaming.BuildDirectory(context)}/{PicardNaming.BuildFileName(context)}.mp3"
        );
    }

    private static string WriteFile(string absolutePath, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, contents);
        return absolutePath.Replace('\\', '/');
    }

    private static MediaFile MediaFileAt(string path) => new() { Path = path };

    [Fact]
    public async Task A_source_in_a_sibling_folder_named_like_the_library_is_moved_to_the_picard_target()
    {
        MusicBrainzReleaseAppends release = Release();
        MusicBrainzTrack track = Track();
        string source = WriteFile(
            Path.Combine(_driveRoot, "Music Unsorted", "batch", "01 Track.mp3"),
            "sibling"
        );
        MediaFile mediaFile = MediaFileAt(source);
        string target = $"{_libraryRoot}/{TargetRelativePath(release, track)}";

        await _manager.RelocateToPicardLayout(
            release,
            track,
            mediaFile,
            _libraryFolder,
            _libraryRoot
        );

        File.Exists(source).Should().BeFalse("the file moved");
        File.ReadAllText(target).Should().Be("sibling");
        mediaFile.Path.Should().Be(target);
    }

    [Fact]
    public async Task A_source_in_an_unrelated_folder_inside_the_guard_root_is_moved()
    {
        MusicBrainzReleaseAppends release = Release();
        MusicBrainzTrack track = Track();
        string source = WriteFile(Path.Combine(_driveRoot, "Incoming", "01 Track.mp3"), "incoming");
        MediaFile mediaFile = MediaFileAt(source);
        string target = $"{_libraryRoot}/{TargetRelativePath(release, track)}";

        await _manager.RelocateToPicardLayout(
            release,
            track,
            mediaFile,
            _libraryFolder,
            _libraryRoot
        );

        File.Exists(source).Should().BeFalse();
        File.ReadAllText(target).Should().Be("incoming");
        mediaFile.Path.Should().Be(target);
    }

    [Fact]
    public async Task A_source_already_inside_the_library_is_still_relocated()
    {
        MusicBrainzReleaseAppends release = Release();
        MusicBrainzTrack track = Track();
        string source = WriteFile(
            Path.Combine(_driveRoot, "Music", "Loose", "01 Track.mp3"),
            "in-library"
        );
        MediaFile mediaFile = MediaFileAt(source);
        string target = $"{_libraryRoot}/{TargetRelativePath(release, track)}";

        await _manager.RelocateToPicardLayout(
            release,
            track,
            mediaFile,
            _libraryFolder,
            _libraryRoot
        );

        File.Exists(source).Should().BeFalse();
        File.ReadAllText(target).Should().Be("in-library");
        mediaFile.Path.Should().Be(target);
    }

    [Fact]
    public async Task A_source_outside_the_guard_root_is_refused_and_stays_where_it_is()
    {
        MusicBrainzReleaseAppends release = Release();
        MusicBrainzTrack track = Track();
        string source = WriteFile(Path.Combine(_outsideRoot, "01 Track.mp3"), "outside");
        MediaFile mediaFile = MediaFileAt(source);
        string target = $"{_libraryRoot}/{TargetRelativePath(release, track)}";

        Func<Task> act = () =>
            _manager.RelocateToPicardLayout(
                release,
                track,
                mediaFile,
                _libraryFolder,
                _libraryRoot
            );

        await act.Should().ThrowAsync<StoragePathNotAllowedException>();
        File.Exists(source).Should().BeTrue("a refused move must leave the file alone");
        File.Exists(target).Should().BeFalse();
        mediaFile.Path.Should().Be(source);
    }

    [Fact]
    public async Task A_missing_source_does_not_displace_the_file_already_at_the_target()
    {
        MusicBrainzReleaseAppends release = Release();
        MusicBrainzTrack track = Track();
        string target = WriteFile(
            Path.Combine(_driveRoot, "Music", TargetRelativePath(release, track)),
            "occupant"
        );
        MediaFile mediaFile = MediaFileAt(
            $"{_driveRoot.Replace('\\', '/')}/Music Unsorted/gone.mp3"
        );

        Func<Task> act = () =>
            _manager.RelocateToPicardLayout(
                release,
                track,
                mediaFile,
                _libraryFolder,
                _libraryRoot
            );

        await act.Should().ThrowAsync<FileNotFoundException>();
        File.ReadAllText(target).Should().Be("occupant");
        Directory
            .GetFiles(Path.GetDirectoryName(target)!)
            .Should()
            .ContainSingle("no .repair-displaced copy may be created for a missing source");
    }

    [Fact]
    public async Task A_source_in_a_lookalike_folder_with_the_same_relative_path_is_not_taken_as_placed()
    {
        MusicBrainzReleaseAppends release = Release();
        MusicBrainzTrack track = Track();
        string source = WriteFile(
            Path.Combine(_driveRoot, "MusicArchive", TargetRelativePath(release, track)),
            "archive"
        );
        MediaFile mediaFile = MediaFileAt(source);
        string target = $"{_libraryRoot}/{TargetRelativePath(release, track)}";

        await _manager.RelocateToPicardLayout(
            release,
            track,
            mediaFile,
            _libraryFolder,
            _libraryRoot
        );

        File.Exists(source).Should().BeFalse("a matching relative tail is not the library target");
        File.ReadAllText(target).Should().Be("archive");
        mediaFile.Path.Should().Be(target);
    }

    [Fact]
    public async Task A_source_already_at_the_exact_target_is_not_moved()
    {
        MusicBrainzReleaseAppends release = Release();
        MusicBrainzTrack track = Track();
        string target = WriteFile(
            Path.Combine(_driveRoot, "Music", TargetRelativePath(release, track)),
            "placed"
        );
        MediaFile mediaFile = MediaFileAt(target);

        await _manager.RelocateToPicardLayout(
            release,
            track,
            mediaFile,
            _libraryFolder,
            _libraryRoot
        );

        File.ReadAllText(target).Should().Be("placed");
        Directory.GetFiles(Path.GetDirectoryName(target)!).Should().ContainSingle();
        mediaFile.Path.Should().Be(target);
    }
}
