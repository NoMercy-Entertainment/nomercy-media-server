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
using NoMercy.Data.DTOs.Specials;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.MediaProcessing.Images;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Data.Repositories;

public class SpecialRepository(MediaContext context, IDbContextFactory<MediaContext> contextFactory)
    : ISpecialRepository
{
    public async Task<List<Special>> GetSpecialsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        List<Special> specials = await context
            .Specials.AsNoTracking()
            .AsSplitQuery()
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m => m!.VideoFiles.Where(v => v.Folder != null))
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.VideoFiles.Where(v => v.Folder != null))
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m =>
                        m!
                            .CertificationMovies.Where(c => c.Certification.Iso31661 == "US")
                            .OrderBy(c => c.CertificationId)
                            .Take(1)
                    )
                        .ThenInclude(c => c.Certification)
            .OrderBy(special => special.TitleSort)
            .ThenBy(special => special.Id)
            .Skip(page * take)
            .Take(take)
            .ToListAsync(ct);

        return specials;
    }

    public Task<List<SpecialCardDto>> GetSpecialCardsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        return context
            .Specials.AsNoTracking()
            .AsSingleQuery()
            .OrderBy(special => special.TitleSort)
            .ThenBy(special => special.Id)
            .Skip(page * take)
            .Take(take)
            .Select(special => new SpecialCardDto
            {
                Id = special.Id,
                Title = special.Title.OrEmpty(),
                TitleSort = special.TitleSort ?? special.Title.OrEmpty(),
                Overview = special.Overview,
                Poster = special.Poster,
                Backdrop = special.Backdrop,
                Logo = special.Logo,
                ColorPalette = special._colorPalette,
                CreatedAt = special.CreatedAt,
                NumberOfItems = special.Items.Count,
                HaveMovies = special.Items.Count(i =>
                    i.Movie != null && i.Movie.VideoFiles.Any(v => v.Folder != null)
                ),
                HaveEpisodes = special.Items.Count(i =>
                    i.Episode != null && i.Episode.VideoFiles.Any(v => v.Folder != null)
                ),
                CertificationRating = special
                    .Items.Where(i => i.Movie != null)
                    .SelectMany(i => i.Movie!.CertificationMovies)
                    .Where(cm => cm.Certification.Iso31661 == "US")
                    .Select(cm => cm.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = special
                    .Items.Where(i => i.Movie != null)
                    .SelectMany(i => i.Movie!.CertificationMovies)
                    .Where(cm => cm.Certification.Iso31661 == "US")
                    .Select(cm => cm.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
    }

    public async Task<List<SpecialCardDto>> GetSpecialItemCardsAsync(
        Guid userId,
        string language,
        string country,
        int take = 1,
        int page = 0,
        CancellationToken ct = default
    )
    {
        // Callers run this next to other queries, so it owns its context.
        await using MediaContext db = await contextFactory.CreateDbContextAsync(ct);
        return await db
            .Specials.AsNoTracking()
            .AsSingleQuery()
            .OrderBy(special => special.TitleSort)
            .ThenBy(special => special.Id)
            .Skip(page * take)
            .Take(take)
            .Select(special => new SpecialCardDto
            {
                Id = special.Id,
                Title = special.Title.OrEmpty(),
                TitleSort = special.TitleSort ?? special.Title.OrEmpty(),
                Overview = special.Overview,
                Poster = special.Poster,
                Backdrop = special.Backdrop,
                Logo = special.Logo,
                ColorPalette = special._colorPalette,
                CreatedAt = special.CreatedAt,
                NumberOfItems = special.Items.Count,
                HaveMovies = special.Items.Count(i =>
                    i.Movie != null && i.Movie.VideoFiles.Any(v => v.Folder != null)
                ),
                HaveEpisodes = special.Items.Count(i =>
                    i.Episode != null && i.Episode.VideoFiles.Any(v => v.Folder != null)
                ),
                CertificationRating = special
                    .Items.Where(i => i.Movie != null)
                    .SelectMany(i => i.Movie!.CertificationMovies)
                    .Where(cm =>
                        cm.Certification.Iso31661 == "US" || cm.Certification.Iso31661 == country
                    )
                    .Select(cm => cm.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = special
                    .Items.Where(i => i.Movie != null)
                    .SelectMany(i => i.Movie!.CertificationMovies)
                    .Where(cm =>
                        cm.Certification.Iso31661 == "US" || cm.Certification.Iso31661 == country
                    )
                    .Select(cm => cm.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
    }

    public Task<SpecialDetailDto?> GetSpecialDetailAsync(
        Guid userId,
        Ulid id,
        CancellationToken ct = default
    )
    {
        return context
            .Specials.AsNoTracking()
            .AsSplitQuery()
            .Where(special => special.Id == id)
            .Select(special => new SpecialDetailDto
            {
                Id = special.Id,
                Title = special.Title.OrEmpty(),
                TitleSort = special.TitleSort ?? special.Title.OrEmpty(),
                Overview = special.Overview,
                Backdrop = special.Backdrop,
                Logo = special.Logo,
                Poster = special.Poster,
                ColorPalette = special._colorPalette.OrEmpty(),
                Favorite = special.SpecialUser.Any(su => su.UserId == userId),
                NumberOfItems = special.Items.Count,
                HaveMovies = special.Items.Count(i =>
                    i.Movie != null && i.Movie.VideoFiles.Any(v => v.Folder != null)
                ),
                HaveEpisodes = special.Items.Count(i =>
                    i.Episode != null && i.Episode.VideoFiles.Any(v => v.Folder != null)
                ),
                Items = special
                    .Items.OrderBy(i => i.Order)
                    .ThenBy(i => i.Id)
                    .Select(i => new SpecialItemRefDto
                    {
                        MovieId = i.MovieId,
                        EpisodeId = i.EpisodeId,
                        TvId = i.Episode != null ? i.Episode.TvId : 0,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
    }

    public static async Task<List<SpecialMovieProjection>> GetSpecialMovieProjectionsAsync(
        MediaContext ctx,
        Guid userId,
        IEnumerable<int> movieIds,
        string country,
        CancellationToken ct = default
    )
    {
        List<int> ids = movieIds.ToList();
        if (ids.Count == 0)
            return [];

        // Scalar fields only — no nested .ToList()/.ToArray() to avoid SQLite APPLY
        List<SpecialMovieProjection> movies = await ctx
            .Movies.AsNoTracking()
            .Where(movie => ids.Contains(movie.Id))
            .Select(movie => new SpecialMovieProjection
            {
                Id = movie.Id,
                Title = movie.Title,
                Overview = movie.Overview,
                Backdrop = movie.Backdrop,
                Poster = movie.Poster,
                ColorPalette = movie._colorPalette.OrEmpty(),
                ReleaseDate = movie.ReleaseDate,
                Runtime = movie.Runtime,
                VoteAverage = movie.VoteAverage,
                Video = movie.Video,
                Logo = movie
                    .Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                VideoFileCount = movie.VideoFiles.Count(v => v.Folder != null),
                CertificationRating = movie
                    .CertificationMovies.Where(cm =>
                        cm.Certification.Iso31661 == country || cm.Certification.Iso31661 == "US"
                    )
                    .Select(cm => cm.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = movie
                    .CertificationMovies.Where(cm =>
                        cm.Certification.Iso31661 == country || cm.Certification.Iso31661 == "US"
                    )
                    .Select(cm => cm.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        if (movies.Count == 0)
            return movies;

        ILookup<int, SpecialGenreProjection> genresLookup = (
            await ctx
                .Movies.AsNoTracking()
                .Where(m => ids.Contains(m.Id))
                .SelectMany(
                    m => m.GenreMovies,
                    (m, gm) =>
                        new
                        {
                            MovieId = m.Id,
                            Id = gm.GenreId,
                            gm.Genre.Name,
                        }
                )
                .ToListAsync(ct)
        ).ToLookup(x => x.MovieId, x => new SpecialGenreProjection { Id = x.Id, Name = x.Name });

        var rawImages = await ctx
            .Movies.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .SelectMany(
                m => m.Images,
                (m, i) =>
                    new
                    {
                        MovieId = m.Id,
                        i.Id,
                        i.Site,
                        i.FilePath,
                        Width = i.Width ?? 0,
                        i.Type,
                        Height = i.Height ?? 0,
                        i.Iso6391,
                        VoteAverage = i.VoteAverage ?? 0,
                        VoteCount = i.VoteCount ?? 0,
                        ColorPalette = i._colorPalette,
                    }
            )
            .Where(i =>
                (i.Type == "backdrop" || i.Type == "poster")
                && (i.Iso6391 == "en" || i.Iso6391 == null)
            )
            .ToListAsync(ct);

        Dictionary<int, List<SpecialImageProjection>> backdropsByMovie = rawImages
            .Where(i => i.Type == "backdrop")
            .GroupBy(i => i.MovieId)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.OrderByDescending(i => i.VoteAverage)
                        .ThenBy(i => i.Id)
                        .Take(2)
                        .Select(i => new SpecialImageProjection
                        {
                            Id = i.Id,
                            Site = i.Site,
                            FilePath = i.FilePath,
                            Width = i.Width,
                            Type = i.Type,
                            Height = i.Height,
                            Iso6391 = i.Iso6391,
                            VoteAverage = i.VoteAverage,
                            VoteCount = i.VoteCount,
                            ColorPalette = i.ColorPalette,
                        })
                        .ToList()
            );

        Dictionary<int, List<SpecialImageProjection>> postersByMovie = rawImages
            .Where(i => i.Type == "poster")
            .GroupBy(i => i.MovieId)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.OrderByDescending(i => i.VoteAverage)
                        .ThenBy(i => i.Id)
                        .Take(2)
                        .Select(i => new SpecialImageProjection
                        {
                            Id = i.Id,
                            Site = i.Site,
                            FilePath = i.FilePath,
                            Width = i.Width,
                            Type = i.Type,
                            Height = i.Height,
                            Iso6391 = i.Iso6391,
                            VoteAverage = i.VoteAverage,
                            VoteCount = i.VoteCount,
                            ColorPalette = i.ColorPalette,
                        })
                        .ToList()
            );

        ILookup<int, SpecialCastProjection> castLookup = (
            await ctx
                .Movies.AsNoTracking()
                .Where(m => ids.Contains(m.Id))
                .SelectMany(
                    m => m.Cast,
                    (m, c) =>
                        new
                        {
                            MovieId = m.Id,
                            PersonId = c.Person.Id,
                            PersonName = c.Person.Name,
                            PersonProfile = c.Person.Profile,
                            PersonKnownForDepartment = c.Person.KnownForDepartment,
                            PersonColorPalette = c.Person._colorPalette,
                            PersonDeathDay = c.Person.DeathDay,
                            PersonGender = c.Person.Gender,
                            c.Role.Character,
                            c.Role.Order,
                        }
                )
                .ToListAsync(ct)
        )
            .GroupBy(x => x.MovieId)
            .SelectMany(
                g => g.OrderBy(x => x.Order).Take(15),
                (g, x) =>
                    new
                    {
                        g.Key,
                        Cast = new SpecialCastProjection
                        {
                            PersonId = x.PersonId,
                            PersonName = x.PersonName,
                            PersonProfile = x.PersonProfile,
                            PersonKnownForDepartment = x.PersonKnownForDepartment,
                            PersonColorPalette = x.PersonColorPalette,
                            PersonDeathDay = x.PersonDeathDay,
                            PersonGender = x.PersonGender,
                            Character = x.Character,
                            Order = x.Order,
                        },
                    }
            )
            .ToLookup(x => x.Key, x => x.Cast);

        ILookup<int, SpecialCrewProjection> crewLookup = (
            await ctx
                .Movies.AsNoTracking()
                .Where(m => ids.Contains(m.Id))
                .SelectMany(
                    m => m.Crew,
                    (m, c) =>
                        new
                        {
                            MovieId = m.Id,
                            PersonId = c.Person.Id,
                            PersonName = c.Person.Name,
                            PersonProfile = c.Person.Profile,
                            PersonKnownForDepartment = c.Person.KnownForDepartment,
                            PersonColorPalette = c.Person._colorPalette,
                            PersonDeathDay = c.Person.DeathDay,
                            PersonGender = c.Person.Gender,
                            c.Job.Task,
                            c.Job.Order,
                        }
                )
                .ToListAsync(ct)
        )
            .GroupBy(x => x.MovieId)
            .SelectMany(
                g => g.OrderBy(x => x.Order).Take(15),
                (g, x) =>
                    new
                    {
                        g.Key,
                        Crew = new SpecialCrewProjection
                        {
                            PersonId = x.PersonId,
                            PersonName = x.PersonName,
                            PersonProfile = x.PersonProfile,
                            PersonKnownForDepartment = x.PersonKnownForDepartment,
                            PersonColorPalette = x.PersonColorPalette,
                            PersonDeathDay = x.PersonDeathDay,
                            PersonGender = x.PersonGender,
                            Task = x.Task,
                            Order = x.Order,
                        },
                    }
            )
            .ToLookup(x => x.Key, x => x.Crew);

        foreach (SpecialMovieProjection movie in movies)
        {
            movie.Genres = genresLookup[movie.Id].ToList();
            movie.Backdrops = backdropsByMovie.GetValueOrDefault(movie.Id, []);
            movie.Posters = postersByMovie.GetValueOrDefault(movie.Id, []);
            movie.Cast = castLookup[movie.Id].ToList();
            movie.Crew = crewLookup[movie.Id].ToList();
        }

        return movies;
    }

    public static async Task<List<SpecialTvProjection>> GetSpecialTvProjectionsAsync(
        MediaContext ctx,
        Guid userId,
        IEnumerable<int> tvIds,
        string country,
        CancellationToken ct = default
    )
    {
        List<int> ids = tvIds.ToList();
        if (ids.Count == 0)
            return [];

        // Scalar fields only — no nested .ToList()/.ToArray() to avoid SQLite APPLY
        List<SpecialTvProjection> tvs = await ctx
            .Tvs.AsNoTracking()
            .Where(tv => ids.Contains(tv.Id))
            .Select(tv => new SpecialTvProjection
            {
                Id = tv.Id,
                Title = tv.Title,
                Overview = tv.Overview,
                Backdrop = tv.Backdrop,
                Poster = tv.Poster,
                ColorPalette = tv._colorPalette.OrEmpty(),
                FirstAirDate = tv.FirstAirDate,
                Duration = tv.Duration,
                VoteAverage = tv.VoteAverage,
                Trailer = tv.Trailer,
                Logo = tv
                    .Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                NumberOfEpisodes = tv.Episodes.Count(e => e.SeasonNumber > 0),
                HaveEpisodes = tv.Episodes.Count(e => e.SeasonNumber > 0 && e.VideoFiles.Any()),
                CertificationRating = tv
                    .CertificationTvs.Where(ct2 =>
                        ct2.Certification.Iso31661 == country || ct2.Certification.Iso31661 == "US"
                    )
                    .Select(ct2 => ct2.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = tv
                    .CertificationTvs.Where(ct2 =>
                        ct2.Certification.Iso31661 == country || ct2.Certification.Iso31661 == "US"
                    )
                    .Select(ct2 => ct2.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        if (tvs.Count == 0)
            return tvs;

        var allEpisodes = await ctx
            .Tvs.AsNoTracking()
            .Where(tv => ids.Contains(tv.Id))
            .SelectMany(
                tv => tv.Episodes,
                (tv, e) =>
                    new
                    {
                        TvId = tv.Id,
                        EpisodeId = e.Id,
                        e.SeasonNumber,
                        Duration = e.VideoFiles.Select(vf => vf.Duration).FirstOrDefault(),
                    }
            )
            .ToListAsync(ct);

        Dictionary<int, int[]> episodeIdsByTv = allEpisodes
            .GroupBy(x => x.TvId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.EpisodeId).ToArray());

        Dictionary<int, List<string?>> episodeDurationsByTv = allEpisodes
            .Where(x => x.SeasonNumber > 0)
            .GroupBy(x => x.TvId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Duration).ToList());

        ILookup<int, SpecialGenreProjection> genresLookup = (
            await ctx
                .Tvs.AsNoTracking()
                .Where(tv => ids.Contains(tv.Id))
                .SelectMany(
                    tv => tv.GenreTvs,
                    (tv, gt) =>
                        new
                        {
                            TvId = tv.Id,
                            Id = gt.GenreId,
                            gt.Genre.Name,
                        }
                )
                .ToListAsync(ct)
        ).ToLookup(x => x.TvId, x => new SpecialGenreProjection { Id = x.Id, Name = x.Name });

        var rawImages = await ctx
            .Tvs.AsNoTracking()
            .Where(tv => ids.Contains(tv.Id))
            .SelectMany(
                tv => tv.Images,
                (tv, i) =>
                    new
                    {
                        TvId = tv.Id,
                        i.Id,
                        i.Site,
                        i.FilePath,
                        Width = i.Width ?? 0,
                        i.Type,
                        Height = i.Height ?? 0,
                        i.Iso6391,
                        VoteAverage = i.VoteAverage ?? 0,
                        VoteCount = i.VoteCount ?? 0,
                        ColorPalette = i._colorPalette,
                    }
            )
            .Where(i =>
                (i.Type == "backdrop" || i.Type == "poster")
                && (i.Iso6391 == "en" || i.Iso6391 == null)
            )
            .ToListAsync(ct);

        Dictionary<int, List<SpecialImageProjection>> backdropsByTv = rawImages
            .Where(i => i.Type == "backdrop")
            .GroupBy(i => i.TvId)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.OrderByDescending(i => i.VoteAverage)
                        .ThenBy(i => i.Id)
                        .Take(2)
                        .Select(i => new SpecialImageProjection
                        {
                            Id = i.Id,
                            Site = i.Site,
                            FilePath = i.FilePath,
                            Width = i.Width,
                            Type = i.Type,
                            Height = i.Height,
                            Iso6391 = i.Iso6391,
                            VoteAverage = i.VoteAverage,
                            VoteCount = i.VoteCount,
                            ColorPalette = i.ColorPalette,
                        })
                        .ToList()
            );

        Dictionary<int, List<SpecialImageProjection>> postersByTv = rawImages
            .Where(i => i.Type == "poster")
            .GroupBy(i => i.TvId)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.OrderByDescending(i => i.VoteAverage)
                        .ThenBy(i => i.Id)
                        .Take(2)
                        .Select(i => new SpecialImageProjection
                        {
                            Id = i.Id,
                            Site = i.Site,
                            FilePath = i.FilePath,
                            Width = i.Width,
                            Type = i.Type,
                            Height = i.Height,
                            Iso6391 = i.Iso6391,
                            VoteAverage = i.VoteAverage,
                            VoteCount = i.VoteCount,
                            ColorPalette = i.ColorPalette,
                        })
                        .ToList()
            );

        ILookup<int, SpecialCastProjection> castLookup = (
            await ctx
                .Tvs.AsNoTracking()
                .Where(tv => ids.Contains(tv.Id))
                .SelectMany(
                    tv => tv.Cast,
                    (tv, c) =>
                        new
                        {
                            TvId = tv.Id,
                            PersonId = c.Person.Id,
                            PersonName = c.Person.Name,
                            PersonProfile = c.Person.Profile,
                            PersonKnownForDepartment = c.Person.KnownForDepartment,
                            PersonColorPalette = c.Person._colorPalette,
                            PersonDeathDay = c.Person.DeathDay,
                            PersonGender = c.Person.Gender,
                            c.Role.Character,
                            c.Role.Order,
                        }
                )
                .ToListAsync(ct)
        )
            .GroupBy(x => x.TvId)
            .SelectMany(
                g => g.OrderBy(x => x.Order).Take(15),
                (g, x) =>
                    new
                    {
                        g.Key,
                        Cast = new SpecialCastProjection
                        {
                            PersonId = x.PersonId,
                            PersonName = x.PersonName,
                            PersonProfile = x.PersonProfile,
                            PersonKnownForDepartment = x.PersonKnownForDepartment,
                            PersonColorPalette = x.PersonColorPalette,
                            PersonDeathDay = x.PersonDeathDay,
                            PersonGender = x.PersonGender,
                            Character = x.Character,
                            Order = x.Order,
                        },
                    }
            )
            .ToLookup(x => x.Key, x => x.Cast);

        ILookup<int, SpecialCrewProjection> crewLookup = (
            await ctx
                .Tvs.AsNoTracking()
                .Where(tv => ids.Contains(tv.Id))
                .SelectMany(
                    tv => tv.Crew,
                    (tv, c) =>
                        new
                        {
                            TvId = tv.Id,
                            PersonId = c.Person.Id,
                            PersonName = c.Person.Name,
                            PersonProfile = c.Person.Profile,
                            PersonKnownForDepartment = c.Person.KnownForDepartment,
                            PersonColorPalette = c.Person._colorPalette,
                            PersonDeathDay = c.Person.DeathDay,
                            PersonGender = c.Person.Gender,
                            c.Job.Task,
                            c.Job.Order,
                        }
                )
                .ToListAsync(ct)
        )
            .GroupBy(x => x.TvId)
            .SelectMany(
                g => g.OrderBy(x => x.Order).Take(15),
                (g, x) =>
                    new
                    {
                        g.Key,
                        Crew = new SpecialCrewProjection
                        {
                            PersonId = x.PersonId,
                            PersonName = x.PersonName,
                            PersonProfile = x.PersonProfile,
                            PersonKnownForDepartment = x.PersonKnownForDepartment,
                            PersonColorPalette = x.PersonColorPalette,
                            PersonDeathDay = x.PersonDeathDay,
                            PersonGender = x.PersonGender,
                            Task = x.Task,
                            Order = x.Order,
                        },
                    }
            )
            .ToLookup(x => x.Key, x => x.Crew);

        foreach (SpecialTvProjection tv in tvs)
        {
            tv.EpisodeIds = episodeIdsByTv.GetValueOrDefault(tv.Id, []);
            tv.EpisodeDurations = episodeDurationsByTv.GetValueOrDefault(tv.Id, []);
            tv.Genres = genresLookup[tv.Id].ToList();
            tv.Backdrops = backdropsByTv.GetValueOrDefault(tv.Id, []);
            tv.Posters = postersByTv.GetValueOrDefault(tv.Id, []);
            tv.Cast = castLookup[tv.Id].ToList();
            tv.Crew = crewLookup[tv.Id].ToList();
        }

        return tvs;
    }

    public async Task<Special?> GetSpecialAsync(
        Guid userId,
        Ulid id,
        CancellationToken ct = default
    )
    {
        return await context
            .Specials.AsNoTracking()
            .AsSplitQuery()
            .Where(special => special.Id == id)
            .Include(special => special.Items.OrderBy(specialItem => specialItem.Order))
                .ThenInclude(specialItem => specialItem.Movie)
                    .ThenInclude(movie => movie!.VideoFiles)
                        .ThenInclude(file =>
                            file.UserData.Where(userData => userData.UserId.Equals(userId))
                        )
            .Include(special => special.Items.OrderBy(specialItem => specialItem.Order))
                .ThenInclude(specialItem => specialItem.Episode)
                    .ThenInclude(movie => movie!.VideoFiles)
                        .ThenInclude(file =>
                            file.UserData.Where(userData => userData.UserId.Equals(userId))
                        )
            .Include(special =>
                special.SpecialUser.Where(specialUser => specialUser.UserId.Equals(userId))
            )
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Special?> GetSpecialWithTvAsync(
        Guid userId,
        Ulid id,
        CancellationToken ct = default
    )
    {
        return await context
            .Specials.AsNoTracking()
            .AsSplitQuery()
            .Where(special => special.Id == id)
            .Include(special => special.Items.OrderBy(specialItem => specialItem.Order))
                .ThenInclude(specialItem => specialItem.Movie)
                    .ThenInclude(movie => movie!.VideoFiles)
                        .ThenInclude(file =>
                            file.UserData.Where(userData => userData.UserId.Equals(userId))
                        )
            .Include(special => special.Items.OrderBy(specialItem => specialItem.Order))
                .ThenInclude(specialItem => specialItem.Episode)
                    .ThenInclude(episode => episode!.Tv)
            .Include(special => special.Items.OrderBy(specialItem => specialItem.Order))
                .ThenInclude(specialItem => specialItem.Episode)
                    .ThenInclude(movie => movie!.VideoFiles)
                        .ThenInclude(file =>
                            file.UserData.Where(userData => userData.UserId.Equals(userId))
                        )
            .Include(special =>
                special.SpecialUser.Where(specialUser => specialUser.UserId.Equals(userId))
            )
            .FirstOrDefaultAsync(ct);
    }

    private static readonly Func<
        MediaContext,
        Guid,
        Ulid,
        Task<Special?>
    > GetSpecialAvailableQuery = EF.CompileAsyncQuery(
        (MediaContext mediaContext, Guid userId, Ulid id) =>
            mediaContext
                .Specials.AsNoTracking()
                .Where(special => special.Id == id)
                .Include(special => special.Items)
                    .ThenInclude(specialItem => specialItem.Movie)
                        .ThenInclude(movie => movie!.VideoFiles)
                            .ThenInclude(file => file.UserData)
                .Include(special => special.Items)
                    .ThenInclude(specialItem => specialItem.Episode)
                        .ThenInclude(episode => episode!.VideoFiles)
                            .ThenInclude(file => file.UserData)
                .FirstOrDefault()
    );

    public Task<Special?> GetSpecialAvailableAsync(Guid userId, Ulid id) =>
        GetSpecialAvailableQuery(context, userId, id);

    public Task<List<Special>> GetSpecialItems(
        Guid userId,
        string? language,
        string country,
        int take = 1,
        int page = 0,
        CancellationToken ct = default
    )
    {
        return context
            .Specials.AsNoTracking()
            .AsSplitQuery()
            .Include(special => special.SpecialUser.Where(su => su.UserId == userId))
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m => m!.VideoFiles.Where(v => v.Folder != null))
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.VideoFiles.Where(v => v.Folder != null))
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m =>
                        m!
                            .CertificationMovies.Where(c =>
                                c.Certification.Iso31661 == "US"
                                || c.Certification.Iso31661 == country
                            )
                            .OrderBy(c => c.CertificationId)
                            .Take(1)
                    )
                        .ThenInclude(c => c.Certification)
            .OrderBy(special => special.TitleSort)
            .ThenBy(special => special.Id)
            .Skip(page * take)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<Special?> GetSpecialPlaylistAsync(
        Guid userId,
        Ulid id,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        return context
            .Specials.AsNoTracking()
            .AsSplitQuery()
            .Where(special => special.Id == id)
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m => m!.Translations.Where(t => t.Iso6391 == language))
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m =>
                        m!
                            .Images.Where(i => i.Type == "logo")
                            .OrderByDescending(i => i.VoteAverage)
                            .ThenBy(i => i.Id)
                            .Take(1)
                    )
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m => m!.VideoFiles.Where(v => v.Folder != null))
                        .ThenInclude(v => v.Metadata)
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m => m!.VideoFiles.Where(v => v.Folder != null))
                        .ThenInclude(v =>
                            v.UserData.Where(ud => ud.UserId == userId && ud.Type == "specials")
                        )
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m => m!.MovieUser.Where(mu => mu.UserId == userId))
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m =>
                        m!
                            .CertificationMovies.Where(c =>
                                c.Certification.Iso31661 == "US"
                                || c.Certification.Iso31661 == country
                            )
                            .OrderBy(c => c.CertificationId)
                            .Take(1)
                    )
                        .ThenInclude(c => c.Certification)
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.Season)
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.Translations.Where(t => t.Iso6391 == language))
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e =>
                        e!
                            .Images.Where(i => i.Type == "logo")
                            .OrderByDescending(i => i.VoteAverage)
                            .ThenBy(i => i.Id)
                            .Take(1)
                    )
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.VideoFiles.Where(v => v.Folder != null))
                        .ThenInclude(v => v.Metadata)
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.VideoFiles.Where(v => v.Folder != null))
                        .ThenInclude(v => v.UserData.Where(ud => ud.UserId == userId))
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.Tv)
                        .ThenInclude(tv => tv.Translations.Where(t => t.Iso6391 == language))
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.Tv)
                        .ThenInclude(tv =>
                            tv.Images.Where(i => i.Type == "logo")
                                .OrderByDescending(i => i.VoteAverage)
                                .ThenBy(i => i.Id)
                                .Take(1)
                        )
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.Tv)
                        .ThenInclude(tv => tv.TvUser.Where(tu => tu.UserId == userId))
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.Tv)
                        .ThenInclude(tv =>
                            tv.CertificationTvs.Where(c =>
                                    c.Certification.Iso31661 == "US"
                                    || c.Certification.Iso31661 == country
                                )
                                .OrderBy(c => c.CertificationId)
                                .Take(1)
                        )
                            .ThenInclude(c => c.Certification)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> AddToWatchListAsync(
        Ulid specialId,
        Guid userId,
        bool add = true,
        CancellationToken ct = default
    )
    {
        Special? special = await context
            .Specials.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == specialId, ct);

        if (special is null)
            return false;

        if (add)
        {
            // Find the first item in the special with a video file (prefer movies)
            SpecialItem? firstItemWithVideo = await context
                .SpecialItems.Where(si => si.SpecialId == specialId)
                .Include(si => si.Movie)
                    .ThenInclude(m => m!.VideoFiles)
                .Include(si => si.Episode)
                    .ThenInclude(e => e!.VideoFiles)
                .OrderBy(si => si.Order)
                .FirstOrDefaultAsync(ct);

            if (firstItemWithVideo is not null)
            {
                VideoFile? videoFile =
                    firstItemWithVideo.Movie?.VideoFiles.FirstOrDefault(vf => vf.Folder != null)
                    ?? firstItemWithVideo.Episode?.VideoFiles.FirstOrDefault(vf =>
                        vf.Folder != null
                    );

                if (videoFile is not null)
                {
                    // Check if userdata already exists for this video file
                    UserData? existingUserData = await context.UserData.FirstOrDefaultAsync(
                        ud => ud.UserId == userId && ud.VideoFileId == videoFile.Id,
                        ct
                    );

                    if (existingUserData is null)
                    {
                        context.UserData.Add(
                            new()
                            {
                                UserId = userId,
                                VideoFileId = videoFile.Id,
                                SpecialId = specialId,
                                Time = 0,
                                LastPlayedDate = DateTime.UtcNow.ToString("o"),
                                Type = MediaTypes.SpecialMediaType,
                            }
                        );
                    }
                }
            }
        }
        else
        {
            // Remove all userdata for this special
            List<UserData> userDataToRemove = await context
                .UserData.Where(ud => ud.UserId == userId && ud.SpecialId == specialId)
                .ToListAsync(ct);

            context.UserData.RemoveRange(userDataToRemove);
        }

        await context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<SpecialItemProjections> GetSpecialItemProjectionsAsync(
        Guid userId,
        IEnumerable<int> movieIds,
        IEnumerable<int> tvIds,
        string country,
        CancellationToken ct = default
    )
    {
        // Movies and TVs run on separate contexts so the two queries can execute
        // in parallel without sharing a (non-thread-safe) DbContext.
        Task<List<SpecialMovieProjection>> moviesTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                return await GetSpecialMovieProjectionsAsync(ctx, userId, movieIds, country, ct);
            },
            ct
        );

        Task<List<SpecialTvProjection>> tvsTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                return await GetSpecialTvProjectionsAsync(ctx, userId, tvIds, country, ct);
            },
            ct
        );

        await Task.WhenAll([moviesTask, tvsTask]);

        return new(moviesTask.Result, tvsTask.Result);
    }

    public async Task<Special?> LikeSpecialAsync(
        Ulid id,
        Guid userId,
        bool like,
        CancellationToken ct = default
    )
    {
        Special? special = await context
            .Specials.AsNoTracking()
            .FirstOrDefaultAsync(special => special.Id == id, ct);

        if (special is null)
            return null;

        if (like)
        {
            await context
                .SpecialUser.Upsert(new(special.Id, userId))
                .On(specialUser => new { specialUser.SpecialId, specialUser.UserId })
                .WhenMatched(specialUser =>
                    new() { SpecialId = specialUser.SpecialId, UserId = specialUser.UserId }
                )
                .RunAsync(ct);
        }
        else
        {
            SpecialUser? specialUser = await context
                .SpecialUser.Where(specialUser =>
                    specialUser.SpecialId == special.Id && specialUser.UserId.Equals(userId)
                )
                .FirstOrDefaultAsync(ct);

            if (specialUser is not null)
                context.SpecialUser.Remove(specialUser);

            await context.SaveChangesAsync(ct);
        }

        return special;
    }

    public async Task<List<Special>> GetAllSpecialsAdminAsync(CancellationToken ct = default)
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.Specials.AsNoTracking().ToListAsync(ct);
    }

    public async Task<Special> CreateSpecialAsync(Guid userId, CancellationToken ct = default)
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        int count = await ctx.Specials.CountAsync(ct);

        Special special = new() { Id = Ulid.NewUlid(), Title = $"special {count}" };

        await ctx
            .Specials.Upsert(special)
            .On(s => new { s.Id })
            .WhenMatched((existing, incoming) => new() { Id = incoming.Id, Title = incoming.Title })
            .RunAsync();

        await ctx
            .SpecialUser.Upsert(new() { SpecialId = special.Id, UserId = userId })
            .On(su => new { su.SpecialId, su.UserId })
            .WhenMatched(
                (existing, incoming) =>
                    new() { SpecialId = incoming.SpecialId, UserId = incoming.UserId }
            )
            .RunAsync(ct);

        return special;
    }

    public Task<Special?> GetSpecialByIdAsync(Ulid id, CancellationToken ct = default)
    {
        return context.Specials.Where(special => special.Id == id).FirstOrDefaultAsync(ct);
    }

    public async Task<Special?> UpdateSpecialAsync(
        Ulid id,
        string? title,
        string? overview,
        string? poster,
        string? backdrop,
        string? logo,
        CancellationToken ct = default
    )
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        Special? special = await ctx.Specials.Where(s => s.Id == id).FirstOrDefaultAsync(ct);

        if (special is null)
            return null;

        if (
            (poster is not null && special.Poster != poster)
            || (backdrop is not null && special.Backdrop != backdrop)
            || (logo is not null && special.Logo != logo)
        )
        {
            special._colorPalette = await MovieDbImageManager.MultiColorPalette([
                new("poster", poster),
                new("backdrop", backdrop),
                new("logo", logo),
            ]);
        }

        if (title is not null)
            special.Title = title;

        if (overview is not null)
            special.Overview = overview;

        if (poster is not null)
            special.Poster = poster;

        if (backdrop is not null)
            special.Backdrop = backdrop;

        if (logo is not null)
            special.Logo = logo;

        await ctx.SaveChangesAsync(ct);

        return special;
    }

    public async Task<Special?> DeleteSpecialAsync(Ulid id, CancellationToken ct = default)
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        Special? special = await ctx.Specials.FindAsync([id], ct);

        if (special is null)
            return null;

        ctx.Specials.Remove(special);
        await ctx.SaveChangesAsync(ct);

        return special;
    }

    public async Task<List<Special>> GetAllSpecialsSortableAsync(CancellationToken ct = default)
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.Specials.AsTracking().ToListAsync(ct);
    }

    public async Task<List<Special>> GetAllSpecialsForRescanAsync(CancellationToken ct = default)
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx.Specials.ToListAsync(ct);
    }

    public async Task<List<SpecialItem>> GetSpecialItemsAdminAsync(
        Ulid id,
        CancellationToken ct = default
    )
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        return await ctx
            .SpecialItems.AsNoTracking()
            .Where(si => si.SpecialId == id)
            .Include(si => si.Movie)
                .ThenInclude(m => m!.VideoFiles)
            .Include(si => si.Episode)
                .ThenInclude(e => e!.Tv)
            .Include(si => si.Episode)
                .ThenInclude(e => e!.VideoFiles)
            .OrderBy(si => si.Order)
            .ToListAsync(ct);
    }

    public async Task<bool> ReplaceSpecialItemsAsync(
        Ulid id,
        List<SpecialItemReplacement> items,
        CancellationToken ct = default
    )
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        Special? special = await ctx.Specials.Where(s => s.Id == id).FirstOrDefaultAsync(ct);

        if (special is null)
            return false;

        List<SpecialItem> existing = await ctx
            .SpecialItems.Where(si => si.SpecialId == id)
            .ToListAsync(ct);

        ctx.SpecialItems.RemoveRange(existing);

        List<SpecialItem> newItems = items
            .Select(item => new SpecialItem
            {
                SpecialId = id,
                Order = item.Order,
                MovieId = item.MediaType == "movie" ? item.MediaId : null,
                EpisodeId = item.MediaType == "episode" ? item.MediaId : null,
            })
            .ToList();

        await ctx.SpecialItems.AddRangeAsync(newItems, ct);
        await ctx.SaveChangesAsync(ct);

        return true;
    }

    public async Task<List<Movie>> SearchMoviesAsync(
        string query,
        int take,
        CancellationToken ct = default
    )
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        string normalized = query.ToLower();
        return await ctx
            .Movies.AsNoTracking()
            .Where(m => m.Title.ToLower().Contains(normalized))
            .Include(m => m.VideoFiles)
            .OrderBy(m => m.Title)
            .ThenBy(m => m.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<List<Episode>> SearchEpisodesAsync(
        string query,
        int take,
        CancellationToken ct = default
    )
    {
        await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
        string normalized = query.ToLower();
        return await ctx
            .Episodes.AsNoTracking()
            .Where(e =>
                (e.Title != null && e.Title.ToLower().Contains(normalized))
                || e.Tv.Title.ToLower().Contains(normalized)
            )
            .Include(e => e.Tv)
            .Include(e => e.VideoFiles)
            .OrderBy(e => e.Tv.Title)
            .ThenBy(e => e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber)
            .Take(take)
            .ToListAsync(ct);
    }
}
