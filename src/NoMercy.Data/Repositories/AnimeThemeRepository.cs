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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;

namespace NoMercy.Data.Repositories;

public class AnimeThemeDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TranslatedName { get; set; }
}

public class AnimeThemeRepository(MediaContext context) : IAnimeThemeRepository
{
    public async Task<bool> AnyThemedTitlesAsync(CancellationToken ct = default) =>
        await context.AnimeThemeTv.AsNoTracking().AnyAsync(ct)
        || await context.AnimeThemeMovie.AsNoTracking().AnyAsync(ct);

    public async Task<List<AnimeThemeWithCountsDto>> GetThemesWithCountsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        // Two steps on purpose: mirrors GenreRepository.GetGenresWithCountsAsync —
        // page the themes first, then aggregate their counts with grouped queries.
        // Do not fold this back into a single projection (SQLite APPLY restriction).
        var themes = await context
            .AnimeThemes.AsNoTracking()
            .Where(theme =>
                theme.AnimeThemeMovies.Any(t =>
                    t.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || theme.AnimeThemeTvShows.Any(t =>
                    t.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .OrderBy(theme => theme.Name)
            .ThenBy(theme => theme.Id)
            .Skip(page * take)
            .Take(take)
            .Select(theme => new
            {
                theme.Id,
                theme.Name,
                TranslatedName = theme
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        if (themes.Count == 0)
            return [];

        List<int> ids = [.. themes.Select(theme => theme.Id)];

        Dictionary<int, int> movieTotals = await context
            .AnimeThemeMovie.AsNoTracking()
            .Where(atm =>
                ids.Contains(atm.AnimeThemeId)
                && atm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .GroupBy(atm => atm.AnimeThemeId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Dictionary<int, int> movieWithVideo = await context
            .AnimeThemeMovie.AsNoTracking()
            .Where(atm =>
                ids.Contains(atm.AnimeThemeId)
                && atm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                && atm.Movie.VideoFiles.Any(v => v.Folder != null)
            )
            .GroupBy(atm => atm.AnimeThemeId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Dictionary<int, int> tvTotals = await context
            .AnimeThemeTv.AsNoTracking()
            .Where(att =>
                ids.Contains(att.AnimeThemeId)
                && att.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .GroupBy(att => att.AnimeThemeId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Dictionary<int, int> tvWithVideo = await context
            .AnimeThemeTv.AsNoTracking()
            .Where(att =>
                ids.Contains(att.AnimeThemeId)
                && att.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                && att.Tv.Episodes.Any(e => e.VideoFiles.Any(v => v.Folder != null))
            )
            .GroupBy(att => att.AnimeThemeId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return
        [
            .. themes.Select(theme => new AnimeThemeWithCountsDto
            {
                Id = theme.Id,
                Name = theme.TranslatedName ?? theme.Name,
                TotalMovies = movieTotals.GetValueOrDefault(theme.Id),
                TotalTvShows = tvTotals.GetValueOrDefault(theme.Id),
                MoviesWithVideo = movieWithVideo.GetValueOrDefault(theme.Id),
                TvShowsWithVideo = tvWithVideo.GetValueOrDefault(theme.Id),
            }),
        ];
    }

    public async Task<(
        AnimeThemeDetailDto? Theme,
        List<HomeMovieCardDto> Movies,
        List<HomeTvCardDto> TvShows
    )> GetThemeCardsAsync(
        Guid userId,
        int id,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        // Mirrors GenreRepository.GetGenreCardsAsync's exact shape.
        AnimeThemeDetailDto? themeDetail = await context
            .AnimeThemes.AsNoTracking()
            .Where(theme => theme.Id == id)
            .Select(theme => new AnimeThemeDetailDto
            {
                Id = theme.Id,
                Name = theme.Name,
                TranslatedName = theme
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Name)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (themeDetail is null)
            return (null, [], []);

        List<HomeMovieCardDto> movies = await context
            .AnimeThemeMovie.AsNoTracking()
            .Where(atm => atm.AnimeThemeId == id)
            .Where(atm => atm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(atm => atm.Movie.VideoFiles.Any(v => v.Folder != null))
            .OrderBy(atm => atm.Movie.TitleSort)
            .ThenBy(atm => atm.MovieId)
            .Skip(page * take)
            .Take(take)
            .Select(atm => new HomeMovieCardDto
            {
                Id = atm.Movie.Id,
                Title = atm.Movie.Title,
                TitleSort = atm.Movie.TitleSort,
                TranslatedTitle = atm
                    .Movie.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = atm.Movie.Overview,
                TranslatedOverview = atm
                    .Movie.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = atm.Movie.Poster,
                Backdrop = atm.Movie.Backdrop,
                Logo = atm
                    .Movie.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                ReleaseDate = atm.Movie.ReleaseDate,
                CreatedAt = atm.Movie.CreatedAt,
                ColorPalette = atm.Movie._colorPalette,
                VideoFileCount = atm.Movie.VideoFiles.Count(v => v.Folder != null),
                CertificationRating = atm
                    .Movie.CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = atm
                    .Movie.CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        List<HomeTvCardDto> tvShows = await context
            .AnimeThemeTv.AsNoTracking()
            .Where(att => att.AnimeThemeId == id)
            .Where(att => att.Tv.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(att =>
                att.Tv.Episodes.Any(e =>
                    (
                        e.VideoFiles.Any(v => v.Folder != null)
                        || e.Tv.Episodes.Any(o =>
                            o.SeasonNumber == e.SeasonNumber
                            && o.VideoFiles.Any(w =>
                                w.Folder != null
                                && w.LastEpisodeNumber != null
                                && o.EpisodeNumber <= e.EpisodeNumber
                                && e.EpisodeNumber <= (w.LastEpisodeNumber ?? 0)
                            )
                        )
                    )
                )
            )
            .OrderBy(att => att.Tv.TitleSort)
            .ThenBy(att => att.TvId)
            .Skip(page * take)
            .Take(take)
            .Select(att => new HomeTvCardDto
            {
                Id = att.Tv.Id,
                Title = att.Tv.Title,
                TitleSort = att.Tv.TitleSort,
                TranslatedTitle = att
                    .Tv.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = att.Tv.Overview,
                TranslatedOverview = att
                    .Tv.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = att.Tv.Poster,
                Backdrop = att.Tv.Backdrop,
                Logo = att
                    .Tv.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                FirstAirDate = att.Tv.FirstAirDate,
                CreatedAt = att.Tv.CreatedAt,
                ColorPalette = att.Tv._colorPalette,
                NumberOfEpisodes = att.Tv.NumberOfEpisodes,
                EpisodesWithVideo = att
                    .Tv.Episodes.Where(e => e.SeasonNumber > 0)
                    .Count(e =>
                        (
                            e.VideoFiles.Any(v => v.Folder != null)
                            || e.Tv.Episodes.Any(o =>
                                o.SeasonNumber == e.SeasonNumber
                                && o.VideoFiles.Any(w =>
                                    w.Folder != null
                                    && w.LastEpisodeNumber != null
                                    && o.EpisodeNumber <= e.EpisodeNumber
                                    && e.EpisodeNumber <= (w.LastEpisodeNumber ?? 0)
                                )
                            )
                        )
                    ),
                CertificationRating = att
                    .Tv.CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = att
                    .Tv.CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return (themeDetail, movies, tvShows);
    }
}
