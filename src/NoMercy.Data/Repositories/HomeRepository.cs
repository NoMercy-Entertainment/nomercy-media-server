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
using NoMercy.Data.Extensions;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Data.Repositories;

public class GenreHomeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TranslatedName { get; set; }
    public List<int> MovieIds { get; set; } = [];
    public List<int> TvIds { get; set; } = [];
}

public class HomeMovieCardDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string TitleSort { get; set; } = string.Empty;
    public string? TranslatedTitle { get; set; }
    public string? Overview { get; set; }
    public string? TranslatedOverview { get; set; }
    public string? Poster { get; set; }
    public string? Backdrop { get; set; }
    public string? Logo { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ColorPalette { get; set; }
    public int VideoFileCount { get; set; }
    public string? CertificationRating { get; set; }
    public string? CertificationCountry { get; set; }
}

public class HomeTvCardDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string TitleSort { get; set; } = string.Empty;
    public string? TranslatedTitle { get; set; }
    public string? Overview { get; set; }
    public string? TranslatedOverview { get; set; }
    public string? Poster { get; set; }
    public string? Backdrop { get; set; }
    public string? Logo { get; set; }
    public DateTime? FirstAirDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ColorPalette { get; set; }
    public int NumberOfEpisodes { get; set; }
    public int EpisodesWithVideo { get; set; }
    public string? CertificationRating { get; set; }
    public string? CertificationCountry { get; set; }
}

public class HomeRepository(MediaContext context, IDbContextFactory<MediaContext> contextFactory)
    : IHomeRepository
{
    public async Task<List<Genre>> GetHome(Guid userId, string? language, int take, int page = 0)
    {
        IOrderedQueryable<Genre> query = context
            .Genres.AsNoTracking()
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id)
            .Where(genre =>
                genre.GenreMovies.Any(g =>
                    g.Movie.Library.LibraryUsers.FirstOrDefault(u => u.UserId.Equals(userId))
                    != null
                )
                || genre.GenreTvShows.Any(g =>
                    g.Tv.Library.LibraryUsers.FirstOrDefault(u => u.UserId.Equals(userId)) != null
                )
            )
            .Include(genre =>
                genre.GenreMovies.Where(genreTv =>
                    genreTv.Movie.VideoFiles.Any(videoFile => videoFile.Folder != null) == true
                )
            )
            .Include(genre =>
                genre.GenreTvShows.Where(genreTv =>
                    genreTv.Tv.Episodes.Any(episode =>
                        episode.VideoFiles.Any(videoFile => videoFile.Folder != null)
                    ) == true
                )
            )
            .Include(movie =>
                movie.Translations.Where(translation => translation.Iso6391 == language)
            )
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id);

        List<Genre> genres = await query.Skip(page * take).Take(take).ToListAsync();

        return genres;
    }

    public async Task<List<HomeTvCardDto>> GetHomeTvs(
        List<int> tvIds,
        string? language,
        string country,
        CancellationToken ct = default
    )
    {
        return await context
            .Tvs.AsNoTracking()
            .Where(tv => tvIds.Contains(tv.Id))
            .Where(tv => tv.Episodes.Any(e => e.VideoFiles.Any()))
            .Select(tv => new HomeTvCardDto
            {
                Id = tv.Id,
                Title = tv.Title,
                TitleSort = tv.TitleSort,
                TranslatedTitle = tv
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = tv.Overview,
                TranslatedOverview = tv
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = tv.Poster,
                Backdrop = tv.Backdrop,
                Logo = tv
                    .Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                FirstAirDate = tv.FirstAirDate,
                CreatedAt = tv.CreatedAt,
                ColorPalette = tv._colorPalette,
                NumberOfEpisodes = tv.NumberOfEpisodes,
                EpisodesWithVideo = tv
                    .Episodes.Where(e => e.SeasonNumber > 0)
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
                CertificationRating = tv
                    .CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = tv
                    .CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
    }

    public async Task<List<HomeMovieCardDto>> GetHomeMovies(
        List<int> movieIds,
        string? language,
        string country,
        CancellationToken ct = default
    )
    {
        return await context
            .Movies.AsNoTracking()
            .Where(movie => movieIds.Contains(movie.Id))
            .Where(movie => movie.VideoFiles.Any())
            .Select(movie => new HomeMovieCardDto
            {
                Id = movie.Id,
                Title = movie.Title,
                TitleSort = movie.TitleSort,
                TranslatedTitle = movie
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = movie.Overview,
                TranslatedOverview = movie
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = movie.Poster,
                Backdrop = movie.Backdrop,
                Logo = movie
                    .Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                ReleaseDate = movie.ReleaseDate,
                CreatedAt = movie.CreatedAt,
                ColorPalette = movie._colorPalette,
                VideoFileCount = movie.VideoFiles.Count(v => v.Folder != null),
                CertificationRating = movie
                    .CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = movie
                    .CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
    }

    public async Task<HashSet<UserData>> GetContinueWatchingAsync(
        Guid userId,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        // Step 1: Project to minimal keys, deduplicate, and get unique UserData IDs
        // This avoids loading full entity trees for duplicates that get thrown away
        List<Ulid> uniqueIds = await context
            .UserData.AsNoTracking()
            .Where(ud => ud.UserId == userId)
            .Where(ud => !ud.RemovedFromContinueWatching)
            .Where(ud =>
                ud.MovieId != null
                || ud.TvId != null
                || ud.CollectionId != null
                || ud.SpecialId != null
            )
            // A resume position can survive the file it was recorded against —
            // the source got deleted or re-imported without a match — and
            // without this the card still renders with nothing to play. Same
            // "has a real file" gate GetHomeMovies/GetHomeTvs apply, extended to
            // the two container shapes (Collection, Special) continue-watching
            // also carries.
            .Where(ud =>
                (ud.Movie != null && ud.Movie.VideoFiles.Any())
                || (ud.Tv != null && ud.Tv.Episodes.Any(e => e.VideoFiles.Any()))
                || (
                    ud.Collection != null
                    && ud.Collection.CollectionMovies.Any(cm => cm.Movie.VideoFiles.Any())
                )
                || (
                    ud.Special != null
                    && ud.Special.Items.Any(item =>
                        (item.Movie != null && item.Movie.VideoFiles.Any())
                        || (item.Episode != null && item.Episode.VideoFiles.Any())
                    )
                )
            )
            .OrderByDescending(ud => ud.LastPlayedDate)
            .ThenByDescending(ud => ud.Id)
            .Select(ud => new
            {
                ud.Id,
                ud.MovieId,
                ud.CollectionId,
                ud.TvId,
                ud.SpecialId,
            })
            .ToListAsync(ct)
            .ContinueWith(
                t =>
                    t.Result.DistinctBy(ud => new
                        {
                            ud.MovieId,
                            ud.CollectionId,
                            ud.TvId,
                            ud.SpecialId,
                        })
                        .Select(ud => ud.Id)
                        .ToList(),
                ct
            );

        if (uniqueIds.Count == 0)
            return [];

        // Step 2: Hydrate only the unique entries with all includes
        List<UserData> userData = await context
            .UserData.AsNoTracking()
            .AsSplitQuery()
            .Where(ud => uniqueIds.Contains(ud.Id))
            .Include(ud => ud.VideoFile)
            // Movie includes - only what CardData needs
            .Include(ud => ud.Movie)
                .ThenInclude(m =>
                    m!
                        .Images.Where(image => image.Type == "logo" && image.Iso6391 == "en")
                        .OrderByDescending(image => image.VoteAverage)
                        .ThenBy(image => image.Id)
                        .Take(1)
                )
            .Include(ud => ud.Movie)
                .ThenInclude(m => m!.VideoFiles)
            .Include(ud => ud.Movie)
                .ThenInclude(m =>
                    m!
                        .CertificationMovies.Where(c =>
                            c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                        )
                        .OrderBy(c => c.CertificationId)
                        .Take(1)
                )
                    .ThenInclude(c => c.Certification)
            // Tv includes - only what CardData needs
            .Include(ud => ud.Tv)
                .ThenInclude(tv =>
                    tv!
                        .Images.Where(image => image.Type == "logo" && image.Iso6391 == "en")
                        .OrderByDescending(image => image.VoteAverage)
                        .ThenBy(image => image.Id)
                        .Take(1)
                )
            .Include(ud => ud.Tv)
                .ThenInclude(tv => tv!.Episodes.Where(e => e.SeasonNumber > 0))
                    .ThenInclude(e => e.VideoFiles)
            .Include(ud => ud.Tv)
                .ThenInclude(tv =>
                    tv!
                        .CertificationTvs.Where(c =>
                            c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                        )
                        .OrderBy(c => c.CertificationId)
                        .Take(1)
                )
                    .ThenInclude(c => c.Certification)
            // Collection includes - only what CardData needs
            .Include(ud => ud.Collection)
                .ThenInclude(c =>
                    c!
                        .Images.Where(image => image.Type == "logo" && image.Iso6391 == "en")
                        .OrderByDescending(image => image.VoteAverage)
                        .ThenBy(image => image.Id)
                        .Take(1)
                )
            .Include(ud => ud.Collection)
                .ThenInclude(c => c!.CollectionMovies)
                    .ThenInclude(cm => cm.Movie)
                        .ThenInclude(m => m.VideoFiles)
            .Include(ud => ud.Collection)
                .ThenInclude(c => c!.CollectionMovies)
                    .ThenInclude(cm => cm.Movie)
                        .ThenInclude(m =>
                            m.CertificationMovies.Where(cert =>
                                    cert.Certification.Iso31661 == "US"
                                    || cert.Certification.Iso31661 == country
                                )
                                .OrderBy(cert => cert.CertificationId)
                                .Take(1)
                        )
                            .ThenInclude(c => c.Certification)
            // Special includes - only what CardData needs
            .Include(ud => ud.Special)
                .ThenInclude(s => s!.Items)
                    .ThenInclude(item => item.Movie)
                        .ThenInclude(m => m!.VideoFiles)
            .Include(ud => ud.Special)
                .ThenInclude(s => s!.Items)
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
            .Include(ud => ud.Special)
                .ThenInclude(s => s!.Items)
                    .ThenInclude(item => item.Episode)
                        .ThenInclude(e => e!.VideoFiles)
            .Include(ud => ud.Special)
                .ThenInclude(s => s!.Items)
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
            .OrderByDescending(ud => ud.LastPlayedDate)
            .ThenByDescending(ud => ud.Id)
            .ToListAsync(ct);

        return [.. userData];
    }

    /// <summary>
    /// Loads the current user's favorited video media — movies, tv shows,
    /// collections and specials — from the per-user like join tables
    /// (<c>MovieUser</c>/<c>TvUser</c>/<c>CollectionUser</c>/<c>SpecialUser</c>).
    /// Each entity is hydrated with exactly the includes its
    /// <c>NmCardDto</c> constructor needs; the caller maps and merges.
    /// Queries run sequentially against the shared context — favorites lists
    /// are small, and EF's DbContext is not safe for concurrent use.
    /// </summary>
    public async Task<FavoritesData> GetFavoritesAsync(
        Guid userId,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        // Callers run this next to other queries, so it owns its context.
        await using MediaContext db = await contextFactory.CreateDbContextAsync(ct);

        List<Movie> movies = await db
            .Movies.AsNoTracking()
            .Where(movie => movie.MovieUser.Any(mu => mu.UserId == userId))
            .Where(movie => movie.VideoFiles.Any())
            .Include(movie => movie.Translations.Where(t => t.Iso6391 == language))
            .Include(movie =>
                movie
                    .Images.Where(image => image.Type == "logo" && image.Iso6391 == "en")
                    .OrderByDescending(image => image.VoteAverage)
                    .ThenBy(image => image.Id)
                    .Take(1)
            )
            .Include(movie => movie.VideoFiles)
            .Include(movie =>
                movie
                    .CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .OrderBy(c => c.CertificationId)
                    .Take(1)
            )
                .ThenInclude(c => c.Certification)
            .ToListAsync(ct);

        List<Tv> tvShows = await db
            .Tvs.AsNoTracking()
            .Where(tv => tv.TvUser.Any(tu => tu.UserId == userId))
            .Where(tv => tv.Episodes.Any(e => e.VideoFiles.Any()))
            .Include(tv => tv.Translations.Where(t => t.Iso6391 == language))
            .Include(tv =>
                tv.Images.Where(image => image.Type == "logo" && image.Iso6391 == "en")
                    .OrderByDescending(image => image.VoteAverage)
                    .ThenBy(image => image.Id)
                    .Take(1)
            )
            .Include(tv => tv.Episodes.Where(episode => episode.SeasonNumber > 0))
                .ThenInclude(episode => episode.VideoFiles)
            .Include(tv =>
                tv.CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .OrderBy(c => c.CertificationId)
                    .Take(1)
            )
                .ThenInclude(c => c.Certification)
            .ToListAsync(ct);

        List<Collection> collections = await db
            .Collections.AsNoTracking()
            .Where(collection => collection.CollectionUser.Any(cu => cu.UserId == userId))
            .Where(collection => collection.CollectionMovies.Any(cm => cm.Movie.VideoFiles.Any()))
            .Include(collection => collection.Translations.Where(t => t.Iso6391 == language))
            .Include(collection =>
                collection
                    .Images.Where(image => image.Type == "logo" && image.Iso6391 == "en")
                    .OrderByDescending(image => image.VoteAverage)
                    .ThenBy(image => image.Id)
                    .Take(1)
            )
            .Include(collection => collection.CollectionMovies)
                .ThenInclude(collectionMovie => collectionMovie.Movie)
                    .ThenInclude(movie => movie.VideoFiles)
            .Include(collection => collection.CollectionMovies)
                .ThenInclude(collectionMovie => collectionMovie.Movie)
                    .ThenInclude(movie =>
                        movie
                            .CertificationMovies.Where(c =>
                                c.Certification.Iso31661 == "US"
                                || c.Certification.Iso31661 == country
                            )
                            .OrderBy(c => c.CertificationId)
                            .Take(1)
                    )
                        .ThenInclude(c => c.Certification)
            .ToListAsync(ct);

        List<Special> specials = await db
            .Specials.AsNoTracking()
            .AsSplitQuery()
            .Where(special => special.SpecialUser.Any(su => su.UserId == userId))
            .Where(special =>
                special.Items.Any(item =>
                    (item.Movie != null && item.Movie.VideoFiles.Any())
                    || (item.Episode != null && item.Episode.VideoFiles.Any())
                )
            )
            .Include(special => special.Items)
                .ThenInclude(item => item.Movie)
                    .ThenInclude(m => m!.VideoFiles)
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
                    .ThenInclude(e => e!.VideoFiles)
            .Include(special => special.Items)
                .ThenInclude(item => item.Episode)
                    .ThenInclude(e => e!.Tv)
            .ToListAsync(ct);

        return new(movies, tvShows, collections, specials);
    }

    public Task<HashSet<Image>> GetScreensaverImagesAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        return context
            .Images.AsNoTracking()
            .Where(image =>
                image.Movie!.Library.LibraryUsers.Any(u => u.UserId == userId)
                || image.Tv!.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .Where(image => image._colorPalette != "")
            .Where(image =>
                (
                    image.Type == "backdrop"
                    && (image.Iso6391 == null || image.Iso6391 == "")
                    && image.Height >= 1080
                ) || (image.Type == "logo" && image.Iso6391 == "en")
            )
            .OrderByDescending(image => image.Width)
            .ThenBy(image => image.Id)
            .ToHashSetAsync(ct);
    }

    public Task<List<Library>> GetLibrariesAsync(Guid userId, CancellationToken ct = default)
    {
        return context
            .Libraries.AsNoTracking()
            .ForUser(userId)
            .Where(library => library.Type != MediaTypes.InboxMediaType)
            .ToListAsync(ct);
    }

    public Task<int> GetAnimeCountAsync(Guid userId, CancellationToken ct = default)
    {
        return context
            .Tvs.AsNoTracking()
            .ForUser(userId)
            .CountAsync(tv => tv.Library.Type == MediaTypes.AnimeMediaType, ct);
    }

    public Task<int> GetMovieCountAsync(Guid userId, CancellationToken ct = default)
    {
        return context
            .Movies.AsNoTracking()
            .ForUser(userId)
            .CountAsync(movie => movie.Library.Type == MediaTypes.MovieMediaType, ct);
    }

    public Task<int> GetTvCountAsync(Guid userId, CancellationToken ct = default)
    {
        return context
            .Tvs.AsNoTracking()
            .ForUser(userId)
            .CountAsync(tv => tv.Library.Type == MediaTypes.TvMediaType, ct);
    }

    public async Task<List<GenreHomeDto>> GetHomeGenresAsync(
        Guid userId,
        string? language,
        int take,
        int page = 0,
        CancellationToken ct = default
    )
    {
        // Step 1: Fetch genre base data with translations — no client-side collections in projection
        // (SQLite does not support APPLY, so .ToList() inside .Select() is forbidden)
        List<Genre> genres = await context
            .Genres.AsNoTracking()
            .Where(genre =>
                genre.GenreMovies.Any(g =>
                    g.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || genre.GenreTvShows.Any(g =>
                    g.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Include(genre => genre.Translations.Where(t => t.Iso6391 == language))
            .Include(genre =>
                genre.GenreMovies.Where(gm =>
                    gm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                    && gm.Movie.VideoFiles.Any()
                )
            )
            .Include(genre =>
                genre.GenreTvShows.Where(gt =>
                    gt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                    && gt.Tv.Episodes.Any(e => e.VideoFiles.Any())
                )
            )
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id)
            .Skip(page * take)
            .Take(take)
            .ToListAsync(ct);

        // Step 2: Project to DTO in memory — safe to call .ToList() on in-memory collections
        return
        [
            .. genres.Select(genre => new GenreHomeDto
            {
                Id = genre.Id,
                Name = genre.Name,
                TranslatedName = genre.Translations.FirstOrDefault()?.Name,
                MovieIds = [.. genre.GenreMovies.Select(gm => gm.MovieId)],
                TvIds = [.. genre.GenreTvShows.Select(gt => gt.TvId)],
            }),
        ];
    }

    public async Task<HomeParallelData> GetHomeParallelDataAsync(
        Guid userId,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        // Each task creates its own DbContext — EF DbContext is not thread-safe
        Task<HashSet<UserData>> continueWatchingTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                HomeRepository repo = new(ctx, contextFactory);
                return await repo.GetContinueWatchingAsync(userId, language, country, ct);
            },
            ct
        );

        Task<List<GenreHomeDto>> genreItemsTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                HomeRepository repo = new(ctx, contextFactory);
                return await repo.GetHomeGenresAsync(
                    userId,
                    language,
                    UiLimits.MaximumItemsPerPage,
                    0,
                    ct
                );
            },
            ct
        );

        Task<List<Library>> librariesTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                HomeRepository repo = new(ctx, contextFactory);
                return await repo.GetLibrariesAsync(userId, ct);
            },
            ct
        );

        Task<int> animeCountTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                HomeRepository repo = new(ctx, contextFactory);
                return await repo.GetAnimeCountAsync(userId, ct);
            },
            ct
        );

        Task<int> movieCountTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                HomeRepository repo = new(ctx, contextFactory);
                return await repo.GetMovieCountAsync(userId, ct);
            },
            ct
        );

        Task<int> tvCountTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                HomeRepository repo = new(ctx, contextFactory);
                return await repo.GetTvCountAsync(userId, ct);
            },
            ct
        );

        await Task.WhenAll([
            continueWatchingTask,
            genreItemsTask,
            librariesTask,
            animeCountTask,
            movieCountTask,
            tvCountTask,
        ]);

        return new(
            continueWatchingTask.Result,
            genreItemsTask.Result,
            librariesTask.Result,
            animeCountTask.Result,
            movieCountTask.Result,
            tvCountTask.Result
        );
    }

    public async Task<HomeTvsAndMoviesData> GetHomeTvsAndMoviesAsync(
        List<int> tvIds,
        List<int> movieIds,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        // Each task creates its own DbContext — EF DbContext is not thread-safe
        Task<List<HomeTvCardDto>> tvDataTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                HomeRepository repo = new(ctx, contextFactory);
                return await repo.GetHomeTvs(tvIds, language, country, ct);
            },
            ct
        );

        Task<List<HomeMovieCardDto>> movieDataTask = Task.Run(
            async () =>
            {
                await using MediaContext ctx = await contextFactory.CreateDbContextAsync(ct);
                HomeRepository repo = new(ctx, contextFactory);
                return await repo.GetHomeMovies(movieIds, language, country, ct);
            },
            ct
        );

        await Task.WhenAll([tvDataTask, movieDataTask]);

        return new(tvDataTask.Result, movieDataTask.Result);
    }

    /// <summary>
    /// Picks the poster and logo for the one title the home screen leads with.
    /// </summary>
    /// <remarks>
    /// TMDB keeps a language-less print of most posters — the artwork with the title lettering
    /// left off. That print plus the title's own logo is what a hero wants: the lettering is
    /// then the real logo at the size and place we choose, and it can be in the viewer's own
    /// language. A print that already carries its title cannot take either a logo or a drawn
    /// title without saying the name twice, so the caller is told which kind it got.
    ///
    /// Deliberately a per-title lookup rather than a column on the carousel queries: a home
    /// payload carries hundreds of cards and exactly one hero, and the widest title here has
    /// nearly three hundred images. Three indexed single-row reads for the one card that needs
    /// them costs less than a join every other card pays for and throws away.
    /// </remarks>
    public async Task<HeroArtwork> GetHeroArtworkAsync(
        int id,
        string mediaType,
        string language,
        CancellationToken ct = default
    )
    {
        IQueryable<Image> images =
            mediaType == MediaTypes.TvMediaType
                ? context.Images.AsNoTracking().Where(image => image.TvId == id)
                : context.Images.AsNoTracking().Where(image => image.MovieId == id);

        string? logo = await PickForLanguageAsync(images, "logo", language, ct);

        string? textlessPoster = await images
            .Where(image => image.Type == "poster" && image.Iso6391 == null)
            .OrderByDescending(image => image.VoteAverage)
            .ThenBy(image => image.Id)
            .Select(image => image.FilePath)
            .FirstOrDefaultAsync(ct);

        if (textlessPoster is not null)
            return new(textlessPoster, true, logo);

        return new(await PickForLanguageAsync(images, "poster", language, ct), false, logo);
    }

    /// <summary>
    /// The best-voted image of a type in the caller's language, falling back to English.
    /// </summary>
    private static async Task<string?> PickForLanguageAsync(
        IQueryable<Image> images,
        string type,
        string language,
        CancellationToken ct
    )
    {
        return await images
            .Where(image => image.Type == type)
            .Where(image => image.Iso6391 == language || image.Iso6391 == "en")
            .OrderByDescending(image => image.Iso6391 == language)
            .ThenByDescending(image => image.VoteAverage)
            .ThenBy(image => image.Id)
            .Select(image => image.FilePath)
            .FirstOrDefaultAsync(ct);
    }
}
