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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.TvShows;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Repositories")]
public class QueueCardMediaRepositoryTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly QueueCardMediaRepository _repository;

    public QueueCardMediaRepositoryTests()
    {
        _context = TestMediaContextFactory.CreateSeededContext();
        _repository = new(_context);
    }

    public void Dispose() => _context.Dispose();

    private (Guid AlbumId, Guid TrackId) AddTrackWithAlbum(string? albumCover, string? trackCover)
    {
        Guid albumId = Guid.NewGuid();
        Guid trackId = Guid.NewGuid();

        // Pointed at the already-tracked seeded rows rather than left on the
        // Album/Track property initializers' own `new()` default: an
        // untracked default Library instance hanging off the navigation
        // property gets inserted as a phantom row and trips the FK check.
        Library library = _context.Libraries.First(l => l.Id == SeedConstants.MovieLibraryId);
        Folder folder = _context.Folders.First(f => f.Id == SeedConstants.MovieFolderId);

        Album album = new()
        {
            Id = albumId,
            Name = "Test Album",
            Cover = albumCover,
            LibraryId = library.Id,
            Library = library,
            FolderId = folder.Id,
            LibraryFolder = folder,
        };
        Track track = new()
        {
            Id = trackId,
            Name = "Test Track",
            Cover = trackCover,
            FolderId = folder.Id,
        };
        _context.Albums.Add(album);
        _context.Tracks.Add(track);
        _context.AlbumTrack.Add(new(albumId, trackId));
        _context.SaveChanges();

        return (albumId, trackId);
    }

    [Fact]
    public async Task GetFoldersWithPresetsAsync_IncludesTheLinkedPreset()
    {
        List<Folder> folders = await _repository.GetFoldersWithPresetsAsync([
            SeedConstants.MovieFolderId,
        ]);

        folders.Should().ContainSingle();
        folders[0].EncodingPresetFolders.Should().ContainSingle();
        folders[0]
            .EncodingPresetFolders.First()
            .Preset!.Id.Should()
            .Be(SeedConstants.EncodingPresetId);
    }

    [Fact]
    public async Task GetMoviesAsync_ReturnsOnlyTheRequestedIds()
    {
        List<Movie> movies = await _repository.GetMoviesAsync([129]);

        movies.Should().ContainSingle();
        movies[0].Id.Should().Be(129);
    }

    [Fact]
    public async Task GetEpisodesWithShowAsync_IncludesTheShow()
    {
        List<Episode> episodes = await _repository.GetEpisodesWithShowAsync([62085]);

        episodes.Should().ContainSingle();
        episodes[0].Tv.Should().NotBeNull();
        episodes[0].Tv!.Id.Should().Be(1399);
    }

    [Fact]
    public async Task GetTracksWithAlbumAsync_IncludesTheAlbumLink()
    {
        (Guid albumId, Guid trackId) = AddTrackWithAlbum(albumCover: null, trackCover: null);

        List<Track> tracks = await _repository.GetTracksWithAlbumAsync([trackId]);

        tracks.Should().ContainSingle();
        tracks[0].AlbumTrack.Should().ContainSingle(link => link.AlbumId == albumId);
    }

    [Fact]
    public async Task GetVideoFilesByHostFoldersAsync_IncludesEpisodeAndMovie()
    {
        List<VideoFile> files = await _repository.GetVideoFilesByHostFoldersAsync([
            "/media/movies/Spirited Away (2001)",
        ]);

        files.Should().ContainSingle();
        files[0].Movie.Should().NotBeNull();
    }

    [Fact]
    public async Task GetEncodedTrackCountsByReleaseAsync_CountsLinkedTracksPerAlbum()
    {
        (Guid albumId, _) = AddTrackWithAlbum(albumCover: null, trackCover: null);

        Dictionary<Guid, int> counts = await _repository.GetEncodedTrackCountsByReleaseAsync([
            albumId,
        ]);

        counts.Should().ContainKey(albumId).WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task GetAlbumCoversAsync_ReturnsTheAlbumsOwnCover()
    {
        (Guid albumId, _) = AddTrackWithAlbum(albumCover: "/cover.jpg", trackCover: null);

        Dictionary<Guid, string?> covers = await _repository.GetAlbumCoversAsync([albumId]);

        covers[albumId].Should().Be("/cover.jpg");
    }

    [Fact]
    public async Task GetFallbackTrackCoversAsync_ReadsTheCoverOffATrack_WhenTheAlbumHasNone()
    {
        (Guid albumId, _) = AddTrackWithAlbum(albumCover: null, trackCover: "/track-cover.jpg");

        Dictionary<Guid, string?> covers = await _repository.GetFallbackTrackCoversAsync([albumId]);

        covers[albumId].Should().Be("/track-cover.jpg");
    }
}
