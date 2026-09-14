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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.DTOs;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Storage;
using NoMercy.NmSystem.Domain;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// Covers the two <see cref="LibraryRepository"/> methods extracted from
/// LibrariesController's <c>RepairTrackMatches</c> and <c>DeleteEncoderProfile</c> actions,
/// which previously ran raw EF queries directly in the controller.
/// </summary>
[Trait("Category", "Characterization")]
public class LibraryRepositoryTrackAndPresetLinkTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly LibraryRepository _repository;
    private readonly SqliteConnection _factoryConnection;

    public LibraryRepositoryTrackAndPresetLinkTests()
    {
        (IDbContextFactory<MediaContext> factory, _factoryConnection) =
            TestMediaContextFactory.CreateSeededFactory();
        _context = factory.CreateDbContext();
        _repository = new(factory);
    }

    private (Ulid LibraryId, Ulid FolderId) SeedMusicLibrary()
    {
        Ulid libraryId = Ulid.NewUlid();
        Ulid folderId = Ulid.NewUlid();

        _context.Libraries.Add(
            new Library
            {
                Id = libraryId,
                Title = "Music",
                Type = MediaTypes.MusicMediaType,
            }
        );
        _context.Folders.Add(
            new Folder
            {
                Id = folderId,
                Path = $"/media/music/{folderId}",
                DriverId = Driver.SystemLocalDriverId,
            }
        );
        _context.FolderLibrary.Add(new(folderId, libraryId));
        _context.SaveChanges();

        return (libraryId, folderId);
    }

    private Guid SeedTrackInLibrary(
        Ulid libraryId,
        Ulid folderId,
        string? hostFolder,
        string? filename
    )
    {
        Guid albumId = Guid.NewGuid();
        Guid trackId = Guid.NewGuid();

        _context.Albums.Add(
            new Album
            {
                Id = albumId,
                Name = "Test Album",
                LibraryId = libraryId,
                FolderId = folderId,
                // Nulled explicitly: the property initializers default these navigations to
                // `new()`, and a reachable-but-unattached default Library/Folder gets picked up
                // by the change tracker as a second entity to insert, overriding the scalar FK.
                Library = null!,
                LibraryFolder = null!,
            }
        );
        _context.Tracks.Add(
            new Track
            {
                Id = trackId,
                Name = "Test Track",
                FolderId = folderId,
                HostFolder = hostFolder,
                Filename = filename,
            }
        );
        _context.AlbumTrack.Add(new(albumId, trackId));
        _context.LibraryTrack.Add(new(libraryId, trackId));
        _context.SaveChanges();

        return albumId;
    }

    [Fact]
    public async Task GetTrackHostFoldersForLibraryAsync_ReturnsOneRowPerTrackedTrack()
    {
        (Ulid libraryId, Ulid folderId) = SeedMusicLibrary();
        Guid albumId1 = SeedTrackInLibrary(libraryId, folderId, "/media/music/Album", "01.flac");
        Guid albumId2 = SeedTrackInLibrary(libraryId, folderId, "/media/music/Album", "02.flac");

        List<TrackHostFolderDto> rows = await _repository.GetTrackHostFoldersForLibraryAsync(
            libraryId
        );

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(row => row.HostFolder == "/media/music/Album");
        rows.Should().OnlyContain(row => row.AlbumId == albumId1 || row.AlbumId == albumId2);
    }

    [Fact]
    public async Task GetTrackHostFoldersForLibraryAsync_ExcludesTracksMissingHostFolderOrFilename()
    {
        (Ulid libraryId, Ulid folderId) = SeedMusicLibrary();
        SeedTrackInLibrary(libraryId, folderId, hostFolder: null, filename: "01.flac");
        SeedTrackInLibrary(libraryId, folderId, hostFolder: "/media/music/Album", filename: null);
        SeedTrackInLibrary(libraryId, folderId, "/media/music/Complete", "01.flac");

        List<TrackHostFolderDto> rows = await _repository.GetTrackHostFoldersForLibraryAsync(
            libraryId
        );

        rows.Should().ContainSingle();
        rows[0].HostFolder.Should().Be("/media/music/Complete");
    }

    [Fact]
    public async Task GetTrackHostFoldersForLibraryAsync_IgnoresTracksFromOtherLibraries()
    {
        (Ulid libraryId, Ulid folderId) = SeedMusicLibrary();
        (Ulid otherLibraryId, Ulid otherFolderId) = SeedMusicLibrary();
        SeedTrackInLibrary(libraryId, folderId, "/media/music/Mine", "01.flac");
        SeedTrackInLibrary(otherLibraryId, otherFolderId, "/media/music/Theirs", "01.flac");

        List<TrackHostFolderDto> rows = await _repository.GetTrackHostFoldersForLibraryAsync(
            libraryId
        );

        rows.Should().ContainSingle();
        rows[0].HostFolder.Should().Be("/media/music/Mine");
    }

    [Fact]
    public async Task DeleteEncodingPresetFolderLinkAsync_RemovesTheMatchingLink()
    {
        int deleted = await _repository.DeleteEncodingPresetFolderLinkAsync(
            SeedConstants.MovieFolderId,
            SeedConstants.EncodingPresetId
        );

        deleted.Should().Be(1);
        bool stillLinked = await _context
            .EncodingPresetFolders.AsNoTracking()
            .AnyAsync(link =>
                link.FolderId == SeedConstants.MovieFolderId
                && link.PresetId == SeedConstants.EncodingPresetId
            );
        stillLinked.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteEncodingPresetFolderLinkAsync_NonMatchingIds_DeletesNothing()
    {
        int deleted = await _repository.DeleteEncodingPresetFolderLinkAsync(
            Ulid.NewUlid(),
            Ulid.NewUlid()
        );

        deleted.Should().Be(0);
        bool stillLinked = await _context
            .EncodingPresetFolders.AsNoTracking()
            .AnyAsync(link =>
                link.FolderId == SeedConstants.MovieFolderId
                && link.PresetId == SeedConstants.EncodingPresetId
            );
        stillLinked.Should().BeTrue();
    }

    public void Dispose()
    {
        _context.Dispose();
        _factoryConnection.Dispose();
    }
}
