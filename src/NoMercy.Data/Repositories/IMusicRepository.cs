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

using NoMercy.Data.DTOs;
using NoMercy.Database.Models.Music;

namespace NoMercy.Data.Repositories;

public interface IMusicRepository
{
    Task<Artist?> GetArtistAsync(Guid userId, Guid id, CancellationToken ct = default);

    Task<List<Artist>> GetArtists(Guid userId, string letter, CancellationToken ct = default);

    Task LikeArtistAsync(Guid userId, Artist artist, bool liked, CancellationToken ct = default);

    Task<Album?> GetAlbumAsync(Guid userId, Guid id, CancellationToken ct = default);

    Task<List<Album>> GetAlbums(Guid userId, string letter, CancellationToken ct = default);

    Task LikeAlbumAsync(Guid userId, Album album, bool liked, CancellationToken ct = default);

    Task<List<AlbumTrack>> GetAlbumTracksForIdsAsync(
        List<Guid> albumIds,
        CancellationToken ct = default
    );

    Task<Track?> GetTrackAsync(Guid id, CancellationToken ct = default);

    Task<List<TrackUser>> GetTracks(Guid userId, CancellationToken ct = default);

    Task LikeTrackAsync(Guid userId, Track track, bool liked, CancellationToken ct = default);

    Task RecordPlaybackAsync(Guid trackId, Guid userId, CancellationToken ct = default);

    Task<Track?> GetTrackWithIncludesAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Audio analysis for the named tracks. Tracks without a completed analysis
    /// are absent from the result rather than returned with null readings, so a
    /// caller can tell "not measured" from "measured as zero".
    /// </summary>
    Task<List<TrackAudioAnalysis>> GetTrackAudioAnalysisAsync(
        IReadOnlyCollection<Guid> trackIds,
        CancellationToken ct = default
    );

    Task<Lyric[]?> UpdateTrackLyricsAsync(
        Track track,
        string lyricsJson,
        CancellationToken ct = default
    );

    Task UpdateTrackLyricsOffsetAsync(Track track, int? offsetMs, CancellationToken ct = default);

    Task<List<CarouselResponseItemDto>> GetCarouselPlaylistsAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<Playlist?> GetPlaylistAsync(Guid userId, Guid id, CancellationToken ct = default);

    Task<List<Album>> GetLatestAlbums(CancellationToken ct = default);

    Task<List<Artist>> GetLatestArtists(CancellationToken ct = default);

    Task<List<MusicGenre>> GetLatestGenres(CancellationToken ct = default);

    Task<List<ArtistTrack>> GetFavoriteArtistAsync(Guid userId, CancellationToken ct = default);

    Task<List<AlbumTrack>> GetFavoriteAlbumAsync(Guid userId, CancellationToken ct = default);

    Task<List<PlaylistTrack>> GetFavoritePlaylistAsync(Guid userId, CancellationToken ct = default);

    Task<List<ArtistUser>> GetFavoriteArtists(Guid userId, CancellationToken ct = default);

    Task<List<AlbumUser>> GetFavoriteAlbums(Guid userId, CancellationToken ct = default);

    Task<List<TrackUser>> GetFavoriteTracks(Guid userId, CancellationToken ct = default);

    Task<List<ArtistTrack>> GetArtistTracksForCollectionAsync(
        List<Guid> artistIds,
        CancellationToken ct = default
    );

    Task<List<Guid>> SearchArtistIdsAsync(string normalizedQuery, CancellationToken ct = default);

    Task<List<Guid>> SearchAlbumIdsAsync(string normalizedQuery, CancellationToken ct = default);

    Task<List<Guid>> SearchPlaylistIdsAsync(string normalizedQuery, CancellationToken ct = default);

    Task<List<Guid>> SearchTrackIdsAsync(string normalizedQuery, CancellationToken ct = default);

    Task<List<Artist>> GetArtistsByIdsAsync(List<Guid> artistIds, CancellationToken ct = default);

    Task<List<Album>> GetAlbumsByIdsAsync(List<Guid> albumIds, CancellationToken ct = default);

    Task<List<Playlist>> GetPlaylistsByIdsAsync(
        List<Guid> playlistIds,
        CancellationToken ct = default
    );

    Task<List<Track>> GetTracksByIdsAsync(List<Guid> trackIds, CancellationToken ct = default);

    // Returns every PlaylistTrack row for the playlist (ownership-scoped to userId),
    // fully hydrated for DTO projection. Deliberately NOT rooted through
    // Playlist.Tracks — see MusicRepository.Playlists.cs for why.
    Task<List<PlaylistTrack>> GetPlaylistTracksAsync(
        Guid userId,
        Guid playlistId,
        CancellationToken ct = default
    );

    // Returns every AlbumTrack row for the album, fully hydrated for DTO projection.
    Task<List<AlbumTrack>> GetAlbumTracksAsync(
        Guid userId,
        Guid albumId,
        CancellationToken ct = default
    );

    // Returns every ArtistTrack row for the artist, fully hydrated for DTO projection.
    Task<List<ArtistTrack>> GetArtistTracksAsync(
        Guid userId,
        Guid artistId,
        CancellationToken ct = default
    );

    // Returns every MusicGenreTrack row for the genre, fully hydrated for DTO projection.
    Task<List<MusicGenreTrack>> GetGenreTracksAsync(
        Guid userId,
        Guid genreId,
        CancellationToken ct = default
    );

    Task<List<ArtistCardDto>> GetArtistCardsAsync(
        Guid userId,
        string letter,
        CancellationToken ct = default
    );

    Task<List<ArtistCardDto>> GetAllArtistCardsAsync(Guid userId, CancellationToken ct = default);

    Task<List<ArtistCardDto>> GetLatestArtistCardsAsync(
        int take = 36,
        CancellationToken ct = default
    );

    Task<List<ArtistCardDto>> GetFavoriteArtistCardsAsync(
        Guid userId,
        int take = 36,
        CancellationToken ct = default
    );

    Task<List<ArtistCardDto>> GetArtistCardsByIdsAsync(
        List<Guid> artistIds,
        CancellationToken ct = default
    );

    Task<List<AlbumCardDto>> GetAlbumCardsAsync(
        Guid userId,
        string letter,
        string language,
        CancellationToken ct = default
    );

    Task<List<AlbumCardDto>> GetAllAlbumCardsAsync(
        Guid userId,
        string language,
        CancellationToken ct = default
    );

    Task<List<AlbumCardDto>> GetLatestAlbumCardsAsync(
        int take = 36,
        CancellationToken ct = default
    );

    Task<List<AlbumCardDto>> GetFavoriteAlbumCardsAsync(
        Guid userId,
        int take = 36,
        CancellationToken ct = default
    );

    Task<List<AlbumCardDto>> GetAlbumCardsByIdsAsync(
        List<Guid> albumIds,
        CancellationToken ct = default
    );

    Task<List<PlaylistCardDto>> GetPlaylistCardsAsync(
        Guid userId,
        int take = 36,
        CancellationToken ct = default
    );

    Task<List<PlaylistCardDto>> GetPlaylistCardsByIdsAsync(
        List<Guid> playlistIds,
        CancellationToken ct = default
    );

    Task<List<MusicGenreCardDto>> GetLatestGenreCardsAsync(
        int take = 36,
        CancellationToken ct = default
    );

    Task<TopMusicItemDto?> GetTopArtistAsync(Guid userId, CancellationToken ct = default);

    Task<TopMusicItemDto?> GetTopAlbumAsync(Guid userId, CancellationToken ct = default);

    Task<TopMusicItemDto?> GetTopPlaylistAsync(Guid userId, CancellationToken ct = default);

    Task<List<Guid>> GetArtistIdsFromAlbumsAsync(
        List<Guid> albumIds,
        CancellationToken ct = default
    );

    Task<List<Guid>> GetArtistIdsFromPlaylistTracksAsync(
        List<Guid> playlistIds,
        CancellationToken ct = default
    );

    Task<List<Guid>> GetArtistIdsFromTracksAsync(
        List<Guid> trackIds,
        CancellationToken ct = default
    );

    Task<List<Guid>> GetAlbumIdsFromTracksAsync(
        List<Guid> trackIds,
        CancellationToken ct = default
    );

    Task<List<SearchTrackCardDto>> SearchTrackCardsAsync(
        List<Guid> trackIds,
        Guid userId,
        string country,
        CancellationToken ct = default
    );

    Task<MusicStartPageData> GetMusicStartPageAsync(Guid userId, CancellationToken ct = default);

    Task<bool> DeleteArtistAsync(Guid id, CancellationToken ct = default);

    Task<Artist?> GetArtistByIdAsync(Guid id, CancellationToken ct = default);

    Task<Album?> GetAlbumByIdAsync(Guid id, CancellationToken ct = default);

    Task<Artist?> GetArtistForEditAsync(Guid id, CancellationToken ct = default);

    Task<Artist?> GetArtistWithLibraryFolderAsync(Guid id, CancellationToken ct = default);

    Task<Album?> GetAlbumForEditAsync(Guid id, CancellationToken ct = default);

    Task<Album?> GetAlbumWithLibraryFolderAsync(Guid id, CancellationToken ct = default);

    Task<bool> PlaylistNameExistsAsync(string name, Guid userId, CancellationToken ct = default);

    Task CreatePlaylistAsync(
        Playlist playlist,
        List<Guid> trackIds,
        CancellationToken ct = default
    );

    Task<Playlist?> GetPlaylistByNameAsync(
        string name,
        Guid userId,
        CancellationToken ct = default
    );

    Task<Playlist?> GetPlaylistForEditAsync(Guid id, Guid userId, CancellationToken ct = default);

    Task<Playlist?> GetPlaylistForCoverAsync(Guid id, Guid userId, CancellationToken ct = default);

    Task<int> DeletePlaylistAsync(Guid id, Guid userId, CancellationToken ct = default);

    Task<int> AddPlaylistTrackAsync(
        Guid playlistId,
        Guid trackId,
        Guid userId,
        CancellationToken ct = default
    );

    Task<int> RemovePlaylistTrackAsync(
        Guid playlistId,
        Guid trackId,
        Guid userId,
        CancellationToken ct = default
    );

    Task<int> UpdateArtistMetadataAsync(
        Guid id,
        string name,
        string? description,
        string cover,
        string colorPalette,
        CancellationToken ct = default
    );

    Task UpdateArtistCoverAsync(
        Guid id,
        string cover,
        string colorPalette,
        CancellationToken ct = default
    );

    Task<int> UpdateAlbumMetadataAsync(
        Guid id,
        string name,
        string? description,
        string cover,
        string colorPalette,
        CancellationToken ct = default
    );

    Task UpdateAlbumCoverAsync(
        Guid id,
        string cover,
        string colorPalette,
        CancellationToken ct = default
    );

    Task<int> UpdatePlaylistMetadataAsync(
        Guid id,
        Guid userId,
        string name,
        string? description,
        string cover,
        string colorPalette,
        CancellationToken ct = default
    );

    Task UpdatePlaylistCoverAsync(
        Guid id,
        Guid userId,
        string cover,
        string colorPalette,
        CancellationToken ct = default
    );

    Task<MusicSearchFullData> SearchMusicFullDataAsync(
        List<Guid> artistIds,
        List<Guid> albumIds,
        List<Guid> playlistIds,
        List<Guid> trackIds,
        CancellationToken ct = default
    );
}

public record MusicSearchFullData(
    List<Artist> Artists,
    List<Album> Albums,
    List<Playlist> Playlists,
    List<Track> Songs
);
