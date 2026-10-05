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

public class AnimeSeasonDetailDto
{
    public int Id { get; set; }
    public int Year { get; set; }
    public string Quarter { get; set; } = string.Empty;
}

public class AnimeSeasonRepository(MediaContext context) : IAnimeSeasonRepository
{
    public async Task<List<AnimeSeasonWithCountsDto>> GetSeasonsWithCountsAsync(
        Guid userId,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        // Two steps on purpose: mirrors GenreRepository.GetGenresWithCountsAsync —
        // page the seasons first, then aggregate their counts with grouped
        // queries. Do not fold this back into a single projection (SQLite APPLY
        // restriction). AnimeSeason has no Translations, so there is no
        // TranslatedName step here.
        var seasons = await context
            .AnimeSeasons.AsNoTracking()
            .Where(season =>
                season.AnimeSeasonMovies.Any(s =>
                    s.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || season.AnimeSeasonTvShows.Any(s =>
                    s.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .OrderByDescending(season => season.Year)
            .ThenBy(season =>
                season.Quarter == "WINTER" ? 1
                : season.Quarter == "SPRING" ? 2
                : season.Quarter == "SUMMER" ? 3
                : season.Quarter == "FALL" ? 4
                : 5
            )
            .ThenBy(season => season.Id)
            .Skip(page * take)
            .Take(take)
            .Select(season => new
            {
                season.Id,
                season.Year,
                season.Quarter,
            })
            .ToListAsync(ct);

        if (seasons.Count == 0)
            return [];

        List<int> ids = [.. seasons.Select(season => season.Id)];

        Dictionary<int, int> movieTotals = await context
            .AnimeSeasonMovie.AsNoTracking()
            .Where(asm =>
                ids.Contains(asm.AnimeSeasonId)
                && asm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .GroupBy(asm => asm.AnimeSeasonId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Dictionary<int, int> movieWithVideo = await context
            .AnimeSeasonMovie.AsNoTracking()
            .Where(asm =>
                ids.Contains(asm.AnimeSeasonId)
                && asm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                && asm.Movie.VideoFiles.Any(v => v.Folder != null)
            )
            .GroupBy(asm => asm.AnimeSeasonId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Dictionary<int, int> tvTotals = await context
            .AnimeSeasonTv.AsNoTracking()
            .Where(ast =>
                ids.Contains(ast.AnimeSeasonId)
                && ast.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .GroupBy(ast => ast.AnimeSeasonId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Dictionary<int, int> tvWithVideo = await context
            .AnimeSeasonTv.AsNoTracking()
            .Where(ast =>
                ids.Contains(ast.AnimeSeasonId)
                && ast.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                && ast.Tv.Episodes.Any(e => e.VideoFiles.Any(v => v.Folder != null))
            )
            .GroupBy(ast => ast.AnimeSeasonId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // The card of a group with no image of its own shows the posters of
        // the titles inside it that the user can play.
        List<GroupPosterRow> posterRows = await GroupItemPosters.WithTextlessPostersAsync(
            context,
            await context
                .AnimeSeasonTv.AsNoTracking()
                .Where(link =>
                    ids.Contains(link.AnimeSeasonId)
                    && link.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                    && link.Tv.Episodes.Any(e => e.VideoFiles.Any(v => v.Folder != null))
                )
                .Select(link => new GroupPosterRow(
                    link.AnimeSeasonId,
                    link.TvId,
                    link.Tv.CreatedAt,
                    link.Tv.TitleSort,
                    null,
                    link.Tv.Poster
                ))
                .ToListAsync(ct),
            await context
                .AnimeSeasonMovie.AsNoTracking()
                .Where(link =>
                    ids.Contains(link.AnimeSeasonId)
                    && link.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                    && link.Movie.VideoFiles.Any(v => v.Folder != null)
                )
                .Select(link => new GroupPosterRow(
                    link.AnimeSeasonId,
                    link.MovieId,
                    link.Movie.CreatedAt,
                    link.Movie.TitleSort,
                    null,
                    link.Movie.Poster
                ))
                .ToListAsync(ct),
            ct
        );

        Dictionary<int, GroupPoster[]> posters = await GroupItemPosters.PickWithPalettesAsync(
            context,
            posterRows,
            ct
        );

        return
        [
            .. seasons.Select(season => new AnimeSeasonWithCountsDto
            {
                Id = season.Id,
                Year = season.Year,
                Quarter = season.Quarter,
                TotalMovies = movieTotals.GetValueOrDefault(season.Id),
                TotalTvShows = tvTotals.GetValueOrDefault(season.Id),
                MoviesWithVideo = movieWithVideo.GetValueOrDefault(season.Id),
                TvShowsWithVideo = tvWithVideo.GetValueOrDefault(season.Id),
                ItemPosters = posters.GetValueOrDefault(season.Id) ?? [],
            }),
        ];
    }

    public async Task<(
        AnimeSeasonDetailDto? Season,
        List<HomeMovieCardDto> Movies,
        List<HomeTvCardDto> TvShows
    )> GetSeasonCardsAsync(
        Guid userId,
        int id,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        // Mirrors GenreRepository.GetGenreCardsAsync's exact shape. AnimeSeason
        // has no Translations, so the detail projection has no language lookup.
        AnimeSeasonDetailDto? seasonDetail = await context
            .AnimeSeasons.AsNoTracking()
            .Where(season => season.Id == id)
            .Select(season => new AnimeSeasonDetailDto
            {
                Id = season.Id,
                Year = season.Year,
                Quarter = season.Quarter,
            })
            .FirstOrDefaultAsync(ct);

        if (seasonDetail is null)
            return (null, [], []);

        List<HomeMovieCardDto> movies = await context
            .AnimeSeasonMovie.AsNoTracking()
            .Where(asm => asm.AnimeSeasonId == id)
            .Where(asm => asm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(asm => asm.Movie.VideoFiles.Any(v => v.Folder != null))
            .OrderBy(asm => asm.Movie.TitleSort)
            .ThenBy(asm => asm.MovieId)
            .Skip(page * take)
            .Take(take)
            .Select(asm => new HomeMovieCardDto
            {
                Id = asm.Movie.Id,
                Title = asm.Movie.Title,
                TitleSort = asm.Movie.TitleSort,
                TranslatedTitle = asm
                    .Movie.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = asm.Movie.Overview,
                TranslatedOverview = asm
                    .Movie.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = asm.Movie.Poster,
                Backdrop = asm.Movie.Backdrop,
                Logo = asm
                    .Movie.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                ReleaseDate = asm.Movie.ReleaseDate,
                CreatedAt = asm.Movie.CreatedAt,
                ColorPalette = asm.Movie._colorPalette,
                VideoFileCount = asm.Movie.VideoFiles.Count(v => v.Folder != null),
                CertificationRating = asm
                    .Movie.CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = asm
                    .Movie.CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        List<HomeTvCardDto> tvShows = await context
            .AnimeSeasonTv.AsNoTracking()
            .Where(ast => ast.AnimeSeasonId == id)
            .Where(ast => ast.Tv.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(ast =>
                ast.Tv.Episodes.Any(e =>
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
            .OrderBy(ast => ast.Tv.TitleSort)
            .ThenBy(ast => ast.TvId)
            .Skip(page * take)
            .Take(take)
            .Select(ast => new HomeTvCardDto
            {
                Id = ast.Tv.Id,
                Title = ast.Tv.Title,
                TitleSort = ast.Tv.TitleSort,
                TranslatedTitle = ast
                    .Tv.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = ast.Tv.Overview,
                TranslatedOverview = ast
                    .Tv.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = ast.Tv.Poster,
                Backdrop = ast.Tv.Backdrop,
                Logo = ast
                    .Tv.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                FirstAirDate = ast.Tv.FirstAirDate,
                CreatedAt = ast.Tv.CreatedAt,
                ColorPalette = ast.Tv._colorPalette,
                NumberOfEpisodes = ast.Tv.NumberOfEpisodes,
                EpisodesWithVideo = ast
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
                CertificationRating = ast
                    .Tv.CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = ast
                    .Tv.CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return (seasonDetail, movies, tvShows);
    }
}
