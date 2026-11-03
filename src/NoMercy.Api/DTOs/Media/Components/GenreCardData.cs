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

using Newtonsoft.Json;
using NoMercy.Api.DTOs.Common;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMGenreCard component - genre category card.
/// </summary>
public record GenreCardData
{
    [JsonProperty("id")]
    public dynamic? Id { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; } = string.Empty;

    // The untranslated name a card's icon/colour lookup keys on, so a Dutch
    // "Actie" card still resolves the same icon as an English "Action" one
    // instead of falling back to the generic default.
    [JsonProperty("icon_key")]
    public string? IconKey { get; set; }

    [JsonProperty("titleSort")]
    public string? TitleSort { get; set; }

    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    [JsonProperty("rating")]
    public RatingClass? Rating { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("quarter")]
    public string? Quarter { get; set; }

    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("content_ratings")]
    public IEnumerable<ContentRating> ContentRatings { get; set; } = [];

    [JsonProperty("have_items")]
    public int? HaveItems { get; set; }

    [JsonProperty("number_of_items")]
    public int? NumberOfItems { get; set; }

    public GenreCardData() { }

    public GenreCardData(Genre genre)
    {
        Id = genre.Id;
        Title = genre.Name;
        TitleSort = genre.Name;
        IconKey = genre.Name;
        Type = "genre";
        Link = new($"/genres/{genre.Id}", UriKind.Relative);
        NumberOfItems = genre.GenreMovies.Count + genre.GenreTvShows.Count;
        HaveItems =
            genre.GenreMovies.Count(gm => gm.Movie.VideoFiles.Any(v => v.Folder != null))
            + genre.GenreTvShows.Count(gt =>
                gt.Tv.Episodes.Any(e =>
                    e.SeasonNumber > 0
                    && (
                        e.VideoFiles.Any(v => v.Folder != null)
                        || e.Tv.Episodes.Any(o =>
                            o.SeasonNumber == e.SeasonNumber
                            && o.VideoFiles.Any(w =>
                                w is { Folder: not null, LastEpisodeNumber: not null }
                                && o.EpisodeNumber <= e.EpisodeNumber
                                && e.EpisodeNumber <= (w.LastEpisodeNumber ?? 0)
                            )
                        )
                    )
                )
            );
    }

    public GenreCardData(MusicGenre musicGenre)
    {
        Id = musicGenre.Id;
        Title = musicGenre.Name.ToTitleCase();
        TitleSort = musicGenre.Name.TitleSort();
        IconKey = musicGenre.Name;
        Type = "genre";
        Link = new($"/music/genres/{musicGenre.Id}", UriKind.Relative);
        NumberOfItems = musicGenre.AlbumMusicGenres.Count + musicGenre.ArtistMusicGenres.Count;
        HaveItems =
            musicGenre.AlbumMusicGenres.Count(ga => ga.Album.AlbumTrack.Count != 0)
            + musicGenre.ArtistMusicGenres.Count(ga => ga.Artist.ArtistTrack.Count != 0);
    }

    public GenreCardData(GenreWithCountsDto dto)
    {
        Id = dto.Id;
        Title = dto.Name.ToTitleCase();
        TitleSort = dto.Name.ToTitleCase();
        IconKey = dto.CanonicalName;
        Type = "genre";
        Link = new($"/genres/{dto.Id}", UriKind.Relative);
        NumberOfItems = dto.TotalMovies + dto.TotalTvShows;
        HaveItems = dto.MoviesWithVideo + dto.TvShowsWithVideo;
    }

    public GenreCardData(AnimeThemeWithCountsDto dto)
    {
        Id = dto.Id;
        Title = dto.Name.ToTitleCase();
        TitleSort = dto.Name.ToTitleCase();
        IconKey = dto.Name;
        Type = "anime-theme";
        Link = new($"/anime/themes/{dto.Id}", UriKind.Relative);
        NumberOfItems = dto.TotalMovies + dto.TotalTvShows;
        HaveItems = dto.MoviesWithVideo + dto.TvShowsWithVideo;
    }

    public GenreCardData(AnimeDemographicWithCountsDto dto)
    {
        Id = dto.Id;
        Title = dto.Name.ToTitleCase();
        TitleSort = dto.Name.ToTitleCase();
        IconKey = dto.Name;
        Type = "anime-demographic";
        Link = new($"/anime/demographics/{dto.Id}", UriKind.Relative);
        NumberOfItems = dto.TotalMovies + dto.TotalTvShows;
        HaveItems = dto.MoviesWithVideo + dto.TvShowsWithVideo;
    }

    public GenreCardData(AnimeSeasonWithCountsDto dto)
    {
        // AnimeSeason has no Translations. Year/Quarter are exposed raw so the
        // client locale-formats the season label instead of getting an
        // editorially-translated English string baked in server-side. Title is
        // a locale-independent non-null fallback, not a display string.
        // TitleSort stays a chronological sort key, not a display string.
        Id = dto.Id;
        Title = $"{dto.Year:D4}-{dto.Quarter}";
        TitleSort = $"{dto.Year:D4}-{QuarterSortIndex(dto.Quarter):D1}";
        Year = dto.Year;
        Quarter = dto.Quarter;
        Type = "anime-season";
        Link = new($"/anime/seasons/{dto.Id}", UriKind.Relative);
        NumberOfItems = dto.TotalMovies + dto.TotalTvShows;
        HaveItems = dto.MoviesWithVideo + dto.TvShowsWithVideo;
    }

    // Matches AniList's own seasonal ordering: Winter -> Spring -> Summer -> Fall.
    private static int QuarterSortIndex(string? quarter) =>
        quarter switch
        {
            "WINTER" => 1,
            "SPRING" => 2,
            "SUMMER" => 3,
            "FALL" => 4,
            _ => 5,
        };
}
