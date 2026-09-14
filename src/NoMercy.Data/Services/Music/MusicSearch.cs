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

using NoMercy.Data.Repositories;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Data.Services.Music;

public sealed record MusicSearchCards(
    List<ArtistCardDto> Artists,
    List<AlbumCardDto> Albums,
    List<PlaylistCardDto> Playlists,
    List<SearchTrackCardDto> Tracks
)
{
    public bool IsEmpty =>
        Artists.Count == 0 && Albums.Count == 0 && Playlists.Count == 0 && Tracks.Count == 0;
}

public static class MusicSearch
{
    /// <summary>
    /// The music cards matching <paramref name="normalizedQuery"/>. An album, playlist or
    /// track match also brings in its artists, and a track match its album.
    /// </summary>
    public static async Task<MusicSearchCards> FindCardsAsync(
        IMusicRepository musicRepository,
        string normalizedQuery,
        Guid userId,
        string country
    )
    {
        // Bound every category. Without this a broad query fans thousands of matches
        // through cross-reference and card projection into a multi-MB payload, while
        // the view only renders the top result, six tracks, and the carousels. Capping
        // the id lists keeps the rendered first-N identical and drops only the tail.
        const int resultCap = UiLimits.SearchResultsPerCategory;

        List<Guid> artistIds = Cap(await musicRepository.SearchArtistIdsAsync(normalizedQuery));
        List<Guid> albumIds = Cap(await musicRepository.SearchAlbumIdsAsync(normalizedQuery));
        List<Guid> playlistIds = Cap(await musicRepository.SearchPlaylistIdsAsync(normalizedQuery));
        List<Guid> trackIds = Cap(await musicRepository.SearchTrackIdsAsync(normalizedQuery));

        List<Guid> additionalArtistIds = [];
        if (albumIds.Count > 0)
            additionalArtistIds.AddRange(
                await musicRepository.GetArtistIdsFromAlbumsAsync(albumIds)
            );
        if (playlistIds.Count > 0)
            additionalArtistIds.AddRange(
                await musicRepository.GetArtistIdsFromPlaylistTracksAsync(playlistIds)
            );
        if (trackIds.Count > 0)
            additionalArtistIds.AddRange(
                await musicRepository.GetArtistIdsFromTracksAsync(trackIds)
            );

        List<Guid> allArtistIds = Cap(artistIds.Union(additionalArtistIds).Distinct());

        List<Guid> additionalAlbumIds =
            trackIds.Count > 0
                ? [.. await musicRepository.GetAlbumIdsFromTracksAsync(trackIds)]
                : [];
        List<Guid> allAlbumIds = Cap(albumIds.Union(additionalAlbumIds).Distinct());

        return new(
            allArtistIds.Count > 0
                ? await musicRepository.GetArtistCardsByIdsAsync(allArtistIds)
                : [],
            allAlbumIds.Count > 0 ? await musicRepository.GetAlbumCardsByIdsAsync(allAlbumIds) : [],
            playlistIds.Count > 0
                ? await musicRepository.GetPlaylistCardsByIdsAsync(playlistIds)
                : [],
            trackIds.Count > 0
                ? await musicRepository.SearchTrackCardsAsync(trackIds, userId, country)
                : []
        );

        static List<Guid> Cap(IEnumerable<Guid> ids) => [.. ids.Take(resultCap)];
    }
}
