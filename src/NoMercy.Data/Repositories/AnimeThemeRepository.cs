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
using NoMercy.Database.Models.Common;

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
        List<ThemeHead> themes = await VisibleThemes(userId)
            .OrderBy(theme => theme.Name)
            .ThenBy(theme => theme.Id)
            .Skip(page * take)
            .Take(take)
            .Select(theme => new ThemeHead(
                theme.Id,
                theme.Name,
                theme.Description,
                theme
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Name)
                    .FirstOrDefault()
            ))
            .ToListAsync(ct);

        return await BuildCardsAsync(userId, themes, ct);
    }

    /// <summary>
    /// The ids of the themes a carousel shows first. Light on purpose: counts
    /// and dates only, no posters.
    /// </summary>
    public async Task<List<int>> GetTopThemeIdsAsync(
        Guid userId,
        AnimeCarouselOrder order,
        DateOnly today,
        int take,
        CancellationToken ct = default
    )
    {
        List<GroupHead> heads = await VisibleThemes(userId)
            .Select(theme => new GroupHead(theme.Id, theme.Name, 0, string.Empty))
            .ToListAsync(ct);

        List<GroupLink> tvLinks = await context
            .AnimeThemeTv.AsNoTracking()
            .Where(link => link.Tv.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(link => new GroupLink(link.AnimeThemeId, link.TvId))
            .ToListAsync(ct);
        List<GroupMovieLink> movieLinks = await context
            .AnimeThemeMovie.AsNoTracking()
            .Where(link =>
                link.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                && link.Movie.VideoFiles.Any(v => v.Folder != null)
            )
            .Select(link => new GroupMovieLink(
                link.AnimeThemeId,
                link.MovieId,
                link.Movie.CreatedAt
            ))
            .ToListAsync(ct);

        List<GroupRankRow> rows = await AnimeGroupRankingQueries.RankRowsAsync(
            context,
            userId,
            heads,
            tvLinks,
            movieLinks,
            ct
        );

        return AnimeGroupRanking.TopIds(rows, order, AnimeGroupKind.Theme, today, take);
    }

    /// <summary>
    /// The cards of the given themes, in the order of <paramref name="ids"/>. A
    /// theme the user cannot see is left out.
    /// </summary>
    public async Task<List<AnimeThemeWithCountsDto>> GetThemesWithCountsByIdsAsync(
        Guid userId,
        string language,
        IReadOnlyList<int> ids,
        CancellationToken ct = default
    )
    {
        List<int> wanted = [.. ids];
        List<ThemeHead> themes = await VisibleThemes(userId)
            .Where(theme => wanted.Contains(theme.Id))
            .Select(theme => new ThemeHead(
                theme.Id,
                theme.Name,
                theme.Description,
                theme
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Name)
                    .FirstOrDefault()
            ))
            .ToListAsync(ct);

        Dictionary<int, AnimeThemeWithCountsDto> cards = (
            await BuildCardsAsync(userId, themes, ct)
        ).ToDictionary(card => card.Id);

        return [.. wanted.Where(cards.ContainsKey).Select(id => cards[id])];
    }

    private sealed record ThemeHead(
        int Id,
        string Name,
        string? Description,
        string? TranslatedName
    );

    private IQueryable<AnimeTheme> VisibleThemes(Guid userId) =>
        context
            .AnimeThemes.AsNoTracking()
            .Where(theme =>
                theme.AnimeThemeMovies.Any(t =>
                    t.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || theme.AnimeThemeTvShows.Any(t =>
                    t.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            );

    private async Task<List<AnimeThemeWithCountsDto>> BuildCardsAsync(
        Guid userId,
        List<ThemeHead> themes,
        CancellationToken ct
    )
    {
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

        Dictionary<int, int> tvTotals = await context
            .AnimeThemeTv.AsNoTracking()
            .Where(att =>
                ids.Contains(att.AnimeThemeId)
                && att.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .GroupBy(att => att.AnimeThemeId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // The playable titles of each group are fetched once, as poster rows:
        // they give both the "with video" counts and the card's posters. The
        // card of a group with no image of its own shows those posters.
        List<GroupLink> tvLinks = await context
            .AnimeThemeTv.AsNoTracking()
            .Where(link =>
                ids.Contains(link.AnimeThemeId)
                && link.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .Select(link => new GroupLink(link.AnimeThemeId, link.TvId))
            .ToListAsync(ct);
        List<GroupPosterRow> tvRows = await GroupItemPosters.PlayableTvRowsAsync(
            context,
            tvLinks,
            ct
        );
        List<GroupPosterRow> movieRows = await context
            .AnimeThemeMovie.AsNoTracking()
            .Where(link =>
                ids.Contains(link.AnimeThemeId)
                && link.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                && link.Movie.VideoFiles.Any(v => v.Folder != null)
            )
            .Select(link => new GroupPosterRow(
                link.AnimeThemeId,
                link.MovieId,
                link.Movie.CreatedAt,
                link.Movie.TitleSort,
                null,
                link.Movie.Poster
            ))
            .ToListAsync(ct);

        Dictionary<int, int> tvWithVideo = tvRows
            .GroupBy(row => row.GroupId)
            .ToDictionary(group => group.Key, group => group.Count());
        Dictionary<int, int> movieWithVideo = movieRows
            .GroupBy(row => row.GroupId)
            .ToDictionary(group => group.Key, group => group.Count());

        List<GroupPosterRow> posterRows = await GroupItemPosters.WithTextlessPostersAsync(
            context,
            tvRows,
            movieRows,
            ct
        );

        Dictionary<int, GroupPoster[]> posters = await GroupItemPosters.PickWithPalettesAsync(
            context,
            posterRows,
            ct
        );

        return
        [
            .. themes.Select(theme => new AnimeThemeWithCountsDto
            {
                Id = theme.Id,
                Name = theme.TranslatedName ?? theme.Name,
                Description = theme.Description,
                TotalMovies = movieTotals.GetValueOrDefault(theme.Id),
                TotalTvShows = tvTotals.GetValueOrDefault(theme.Id),
                MoviesWithVideo = movieWithVideo.GetValueOrDefault(theme.Id),
                TvShowsWithVideo = tvWithVideo.GetValueOrDefault(theme.Id),
                ItemPosters = posters.GetValueOrDefault(theme.Id) ?? [],
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
