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

public class AnimeDemographicDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TranslatedName { get; set; }
}

public class AnimeDemographicRepository(MediaContext context) : IAnimeDemographicRepository
{
    public async Task<List<AnimeDemographicWithCountsDto>> GetDemographicsWithCountsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        // Two steps on purpose: mirrors GenreRepository.GetGenresWithCountsAsync —
        // page the demographics first, then aggregate their counts with grouped
        // queries. Do not fold this back into a single projection (SQLite APPLY
        // restriction).
        List<DemographicHead> demographics = await VisibleDemographics(userId)
            .OrderBy(demographic => demographic.Name)
            .ThenBy(demographic => demographic.Id)
            .Skip(page * take)
            .Take(take)
            .Select(demographic => new DemographicHead(
                demographic.Id,
                demographic.Name,
                demographic
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Name)
                    .FirstOrDefault()
            ))
            .ToListAsync(ct);

        return await BuildCardsAsync(userId, demographics, ct);
    }

    /// <summary>
    /// The ids of the demographics a carousel shows first. Light on purpose:
    /// counts and dates only, no posters.
    /// </summary>
    public async Task<List<int>> GetTopDemographicIdsAsync(
        Guid userId,
        AnimeCarouselOrder order,
        DateOnly today,
        int take,
        CancellationToken ct = default
    )
    {
        List<GroupHead> heads = await VisibleDemographics(userId)
            .Select(demographic => new GroupHead(demographic.Id, demographic.Name, 0, string.Empty))
            .ToListAsync(ct);

        List<GroupLink> tvLinks = await context
            .AnimeDemographicTv.AsNoTracking()
            .Where(link => link.Tv.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Select(link => new GroupLink(link.AnimeDemographicId, link.TvId))
            .ToListAsync(ct);
        List<GroupMovieLink> movieLinks = await context
            .AnimeDemographicMovie.AsNoTracking()
            .Where(link =>
                link.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                && link.Movie.VideoFiles.Any(v => v.Folder != null)
            )
            .Select(link => new GroupMovieLink(
                link.AnimeDemographicId,
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

        return AnimeGroupRanking.TopIds(rows, order, AnimeGroupKind.Demographic, today, take);
    }

    /// <summary>
    /// The cards of the given demographics, in the order of
    /// <paramref name="ids"/>. A demographic the user cannot see is left out.
    /// </summary>
    public async Task<List<AnimeDemographicWithCountsDto>> GetDemographicsWithCountsByIdsAsync(
        Guid userId,
        string language,
        IReadOnlyList<int> ids,
        CancellationToken ct = default
    )
    {
        List<int> wanted = [.. ids];
        List<DemographicHead> demographics = await VisibleDemographics(userId)
            .Where(demographic => wanted.Contains(demographic.Id))
            .Select(demographic => new DemographicHead(
                demographic.Id,
                demographic.Name,
                demographic
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Name)
                    .FirstOrDefault()
            ))
            .ToListAsync(ct);

        Dictionary<int, AnimeDemographicWithCountsDto> cards = (
            await BuildCardsAsync(userId, demographics, ct)
        ).ToDictionary(card => card.Id);

        return [.. wanted.Where(cards.ContainsKey).Select(id => cards[id])];
    }

    private sealed record DemographicHead(int Id, string Name, string? TranslatedName);

    private IQueryable<AnimeDemographic> VisibleDemographics(Guid userId) =>
        context
            .AnimeDemographics.AsNoTracking()
            .Where(demographic =>
                demographic.AnimeDemographicMovies.Any(d =>
                    d.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || demographic.AnimeDemographicTvShows.Any(d =>
                    d.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            );

    private async Task<List<AnimeDemographicWithCountsDto>> BuildCardsAsync(
        Guid userId,
        List<DemographicHead> demographics,
        CancellationToken ct
    )
    {
        if (demographics.Count == 0)
            return [];

        List<int> ids = [.. demographics.Select(demographic => demographic.Id)];

        Dictionary<int, int> movieTotals = await context
            .AnimeDemographicMovie.AsNoTracking()
            .Where(adm =>
                ids.Contains(adm.AnimeDemographicId)
                && adm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .GroupBy(adm => adm.AnimeDemographicId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Dictionary<int, int> tvTotals = await context
            .AnimeDemographicTv.AsNoTracking()
            .Where(adt =>
                ids.Contains(adt.AnimeDemographicId)
                && adt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .GroupBy(adt => adt.AnimeDemographicId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // The playable titles of each group are fetched once, as poster rows:
        // they give both the "with video" counts and the card's posters. The
        // card of a group with no image of its own shows those posters.
        List<GroupLink> tvLinks = await context
            .AnimeDemographicTv.AsNoTracking()
            .Where(link =>
                ids.Contains(link.AnimeDemographicId)
                && link.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .Select(link => new GroupLink(link.AnimeDemographicId, link.TvId))
            .ToListAsync(ct);
        List<GroupPosterRow> tvRows = await GroupItemPosters.PlayableTvRowsAsync(
            context,
            tvLinks,
            ct
        );
        List<GroupPosterRow> movieRows = await context
            .AnimeDemographicMovie.AsNoTracking()
            .Where(link =>
                ids.Contains(link.AnimeDemographicId)
                && link.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                && link.Movie.VideoFiles.Any(v => v.Folder != null)
            )
            .Select(link => new GroupPosterRow(
                link.AnimeDemographicId,
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
            .. demographics.Select(demographic => new AnimeDemographicWithCountsDto
            {
                Id = demographic.Id,
                Name = demographic.TranslatedName ?? demographic.Name,
                TotalMovies = movieTotals.GetValueOrDefault(demographic.Id),
                TotalTvShows = tvTotals.GetValueOrDefault(demographic.Id),
                MoviesWithVideo = movieWithVideo.GetValueOrDefault(demographic.Id),
                TvShowsWithVideo = tvWithVideo.GetValueOrDefault(demographic.Id),
                ItemPosters = posters.GetValueOrDefault(demographic.Id) ?? [],
            }),
        ];
    }

    public async Task<(
        AnimeDemographicDetailDto? Demographic,
        List<HomeMovieCardDto> Movies,
        List<HomeTvCardDto> TvShows
    )> GetDemographicCardsAsync(
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
        AnimeDemographicDetailDto? demographicDetail = await context
            .AnimeDemographics.AsNoTracking()
            .Where(demographic => demographic.Id == id)
            .Select(demographic => new AnimeDemographicDetailDto
            {
                Id = demographic.Id,
                Name = demographic.Name,
                TranslatedName = demographic
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Name)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (demographicDetail is null)
            return (null, [], []);

        List<HomeMovieCardDto> movies = await context
            .AnimeDemographicMovie.AsNoTracking()
            .Where(adm => adm.AnimeDemographicId == id)
            .Where(adm => adm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(adm => adm.Movie.VideoFiles.Any(v => v.Folder != null))
            .OrderBy(adm => adm.Movie.TitleSort)
            .ThenBy(adm => adm.MovieId)
            .Skip(page * take)
            .Take(take)
            .Select(adm => new HomeMovieCardDto
            {
                Id = adm.Movie.Id,
                Title = adm.Movie.Title,
                TitleSort = adm.Movie.TitleSort,
                TranslatedTitle = adm
                    .Movie.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = adm.Movie.Overview,
                TranslatedOverview = adm
                    .Movie.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = adm.Movie.Poster,
                Backdrop = adm.Movie.Backdrop,
                Logo = adm
                    .Movie.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                ReleaseDate = adm.Movie.ReleaseDate,
                CreatedAt = adm.Movie.CreatedAt,
                ColorPalette = adm.Movie._colorPalette,
                VideoFileCount = adm.Movie.VideoFiles.Count(v => v.Folder != null),
                CertificationRating = adm
                    .Movie.CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = adm
                    .Movie.CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        List<HomeTvCardDto> tvShows = await context
            .AnimeDemographicTv.AsNoTracking()
            .Where(adt => adt.AnimeDemographicId == id)
            .Where(adt => adt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(adt =>
                adt.Tv.Episodes.Any(e =>
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
            .OrderBy(adt => adt.Tv.TitleSort)
            .ThenBy(adt => adt.TvId)
            .Skip(page * take)
            .Take(take)
            .Select(adt => new HomeTvCardDto
            {
                Id = adt.Tv.Id,
                Title = adt.Tv.Title,
                TitleSort = adt.Tv.TitleSort,
                TranslatedTitle = adt
                    .Tv.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = adt.Tv.Overview,
                TranslatedOverview = adt
                    .Tv.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = adt.Tv.Poster,
                Backdrop = adt.Tv.Backdrop,
                Logo = adt
                    .Tv.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                FirstAirDate = adt.Tv.FirstAirDate,
                CreatedAt = adt.Tv.CreatedAt,
                ColorPalette = adt.Tv._colorPalette,
                NumberOfEpisodes = adt.Tv.NumberOfEpisodes,
                EpisodesWithVideo = adt
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
                CertificationRating = adt
                    .Tv.CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = adt
                    .Tv.CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return (demographicDetail, movies, tvShows);
    }
}
