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
using NoMercy.Database.Models.Music;

namespace NoMercy.Data.Repositories;

public class GenreDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TranslatedName { get; set; }
}

public class GenreWithCountsDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // The untranslated genre name, e.g. "Action" even when Name is "Actie".
    // Client-side icon/colour lookups switch on this literal English name, so
    // it has to survive alongside the localized Name a translated response
    // replaces it with.
    public string CanonicalName { get; set; } = string.Empty;
    public int TotalMovies { get; set; }
    public int TotalTvShows { get; set; }
    public int MoviesWithVideo { get; set; }
    public int TvShowsWithVideo { get; set; }
}

public class GenreRepository(MediaContext context) : IGenreRepository
{
    private static readonly string[] Letters =
    [
        "*",
        "#",
        "'",
        "\"",
        "1",
        "2",
        "3",
        "4",
        "5",
        "6",
        "7",
        "8",
        "9",
        "0",
    ];

    public async Task<Genre?> GetGenreAsync(
        Guid userId,
        int id,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        return await context
            .Genres.AsNoTracking()
            .Where(genre => genre.Id == id)
            .Include(genre => genre.Translations.Where(t => t.Iso6391 == language))
            .Include(genre =>
                genre
                    .GenreMovies.Where(gm =>
                        gm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                    )
                    .Where(gm => gm.Movie.VideoFiles.Any(v => v.Folder != null))
                    .OrderBy(gm => gm.MovieId)
                    .Take(take)
            )
                .ThenInclude(gm => gm.Movie)
                    .ThenInclude(m => m.Translations.Where(t => t.Iso6391 == language))
            .Include(genre => genre.GenreMovies)
                .ThenInclude(gm => gm.Movie)
                    .ThenInclude(m => m.VideoFiles.Where(v => v.Folder != null))
            .Include(genre => genre.GenreMovies)
                .ThenInclude(gm => gm.Movie)
                    .ThenInclude(m =>
                        m.Images.Where(i => i.Type == "logo")
                            .OrderByDescending(i => i.VoteAverage)
                            .ThenBy(i => i.Id)
                            .Take(1)
                    )
            .Include(genre => genre.GenreMovies)
                .ThenInclude(gm => gm.Movie)
                    .ThenInclude(m =>
                        m.CertificationMovies.Where(c =>
                                c.Certification.Iso31661 == "US"
                                || c.Certification.Iso31661 == country
                            )
                            .OrderBy(c => c.CertificationId)
                            .Take(1)
                    )
                        .ThenInclude(c => c.Certification)
            .Include(genre =>
                genre
                    .GenreTvShows.Where(gt =>
                        gt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                    )
                    .Where(gt =>
                        gt.Tv.Episodes.Any(e =>
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
                    .OrderBy(gt => gt.TvId)
                    .Take(take)
            )
                .ThenInclude(gt => gt.Tv)
                    .ThenInclude(tv => tv.Translations.Where(t => t.Iso6391 == language))
            .Include(genre => genre.GenreTvShows)
                .ThenInclude(gt => gt.Tv)
                    .ThenInclude(tv =>
                        tv.Episodes.Where(e =>
                            e.SeasonNumber > 0
                            && (
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
                        .ThenInclude(e => e.VideoFiles.Where(v => v.Folder != null))
            .Include(genre => genre.GenreTvShows)
                .ThenInclude(gt => gt.Tv)
                    .ThenInclude(tv =>
                        tv.Images.Where(i => i.Type == "logo")
                            .OrderByDescending(i => i.VoteAverage)
                            .ThenBy(i => i.Id)
                            .Take(1)
                    )
            .Include(genre => genre.GenreTvShows)
                .ThenInclude(gt => gt.Tv)
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

    public async Task<(
        GenreDetailDto? Genre,
        List<HomeMovieCardDto> Movies,
        List<HomeTvCardDto> TvShows
    )> GetGenreCardsAsync(
        Guid userId,
        int id,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        GenreDetailDto? genreDetail = await context
            .Genres.AsNoTracking()
            .Where(genre => genre.Id == id)
            .Select(genre => new GenreDetailDto
            {
                Id = genre.Id,
                Name = genre.Name,
                TranslatedName = genre
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Name)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (genreDetail is null)
            return (null, [], []);

        List<HomeMovieCardDto> movies = await context
            .GenreMovie.AsNoTracking()
            .Where(gm => gm.GenreId == id)
            .Where(gm => gm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(gm => gm.Movie.VideoFiles.Any(v => v.Folder != null))
            .OrderBy(gm => gm.Movie.TitleSort)
            .ThenBy(gm => gm.MovieId)
            .Skip(page * take)
            .Take(take)
            .Select(gm => new HomeMovieCardDto
            {
                Id = gm.Movie.Id,
                Title = gm.Movie.Title,
                TitleSort = gm.Movie.TitleSort,
                TranslatedTitle = gm
                    .Movie.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = gm.Movie.Overview,
                TranslatedOverview = gm
                    .Movie.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = gm.Movie.Poster,
                Backdrop = gm.Movie.Backdrop,
                Logo = gm
                    .Movie.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                ReleaseDate = gm.Movie.ReleaseDate,
                CreatedAt = gm.Movie.CreatedAt,
                ColorPalette = gm.Movie._colorPalette,
                VideoFileCount = gm.Movie.VideoFiles.Count(v => v.Folder != null),
                CertificationRating = gm
                    .Movie.CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = gm
                    .Movie.CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        List<HomeTvCardDto> tvShows = await context
            .GenreTv.AsNoTracking()
            .Where(gt => gt.GenreId == id)
            .Where(gt => gt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId))
            .Where(gt =>
                gt.Tv.Episodes.Any(e =>
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
            .OrderBy(gt => gt.Tv.TitleSort)
            .ThenBy(gt => gt.TvId)
            .Skip(page * take)
            .Take(take)
            .Select(gt => new HomeTvCardDto
            {
                Id = gt.Tv.Id,
                Title = gt.Tv.Title,
                TitleSort = gt.Tv.TitleSort,
                TranslatedTitle = gt
                    .Tv.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Title)
                    .FirstOrDefault(),
                Overview = gt.Tv.Overview,
                TranslatedOverview = gt
                    .Tv.Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Overview)
                    .FirstOrDefault(),
                Poster = gt.Tv.Poster,
                Backdrop = gt.Tv.Backdrop,
                Logo = gt
                    .Tv.Images.Where(i => i.Type == "logo" && i.Iso6391 == "en")
                    .Select(i => i.FilePath)
                    .FirstOrDefault(),
                FirstAirDate = gt.Tv.FirstAirDate,
                CreatedAt = gt.Tv.CreatedAt,
                ColorPalette = gt.Tv._colorPalette,
                NumberOfEpisodes = gt.Tv.NumberOfEpisodes,
                EpisodesWithVideo = gt
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
                CertificationRating = gt
                    .Tv.CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Rating)
                    .FirstOrDefault(),
                CertificationCountry = gt
                    .Tv.CertificationTvs.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .Select(c => c.Certification.Iso31661)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return (genreDetail, movies, tvShows);
    }

    public Task<List<Genre>> GetGenres(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        return context
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
                )
            )
            .Include(genre =>
                genre.GenreTvShows.Where(gt =>
                    gt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id)
            .Skip(page * take)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<List<GenreWithCountsDto>> GetGenresWithCountsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        // Two steps on purpose: computing the four counts as correlated subqueries
        // inside one projection makes SQLite scan the join tables once per genre.
        // Instead, page the genres first, then aggregate their counts with grouped
        // queries. Do not fold this back into a single projection.
        var genres = await context
            .Genres.AsNoTracking()
            .Where(genre =>
                genre.GenreMovies.Any(g =>
                    g.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || genre.GenreTvShows.Any(g =>
                    g.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id)
            .Skip(page * take)
            .Take(take)
            .Select(genre => new
            {
                genre.Id,
                genre.Name,
                TranslatedName = genre
                    .Translations.Where(t => t.Iso6391 == language)
                    .Select(t => t.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        if (genres.Count == 0)
            return [];

        List<int> ids = [.. genres.Select(genre => genre.Id)];

        Dictionary<int, int> movieTotals = await context
            .GenreMovie.AsNoTracking()
            .Where(gm =>
                ids.Contains(gm.GenreId)
                && gm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .GroupBy(gm => gm.GenreId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Dictionary<int, int> movieWithVideo = await context
            .GenreMovie.AsNoTracking()
            .Where(gm =>
                ids.Contains(gm.GenreId)
                && gm.Movie.Library.LibraryUsers.Any(u => u.UserId == userId)
                && gm.Movie.VideoFiles.Any(v => v.Folder != null)
            )
            .GroupBy(gm => gm.GenreId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        Dictionary<int, int> tvTotals = await context
            .GenreTv.AsNoTracking()
            .Where(gt =>
                ids.Contains(gt.GenreId) && gt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
            )
            .GroupBy(gt => gt.GenreId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // A multi-episode video file lives on its own episode, which therefore already
        // passes the direct "has a video file" test. The old nested self-join over
        // LastEpisodeNumber never changed this TV-level count, so it is dropped.
        Dictionary<int, int> tvWithVideo = await context
            .GenreTv.AsNoTracking()
            .Where(gt =>
                ids.Contains(gt.GenreId)
                && gt.Tv.Library.LibraryUsers.Any(u => u.UserId == userId)
                && gt.Tv.Episodes.Any(e => e.VideoFiles.Any(v => v.Folder != null))
            )
            .GroupBy(gt => gt.GenreId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return
        [
            .. genres.Select(genre => new GenreWithCountsDto
            {
                Id = genre.Id,
                Name = genre.TranslatedName ?? genre.Name,
                CanonicalName = genre.Name,
                TotalMovies = movieTotals.GetValueOrDefault(genre.Id),
                TotalTvShows = tvTotals.GetValueOrDefault(genre.Id),
                MoviesWithVideo = movieWithVideo.GetValueOrDefault(genre.Id),
                TvShowsWithVideo = tvWithVideo.GetValueOrDefault(genre.Id),
            }),
        ];
    }

    public Task<List<MusicGenre>> GetMusicGenresAsync(Guid userId, CancellationToken ct = default)
    {
        return context
            .MusicGenres.AsNoTracking()
            .Where(genre =>
                genre.AlbumMusicGenres.Any(g =>
                    g.Album.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || genre.ArtistMusicGenres.Any(g =>
                    g.Artist.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Where(genre => genre.MusicGenreTracks.Any())
            .Include(genre => genre.MusicGenreTracks)
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id)
            .ToListAsync(ct);
    }

    public async Task<List<MusicGenreCardDto>> GetMusicGenreCardsAsync(
        Guid userId,
        CancellationToken ct = default
    )
    {
        List<MusicGenreCardDto> genres = await context
            .MusicGenres.AsNoTracking()
            .Where(genre =>
                genre.AlbumMusicGenres.Any(g =>
                    g.Album.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || genre.ArtistMusicGenres.Any(g =>
                    g.Artist.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Where(genre => genre.MusicGenreTracks.Any())
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id)
            .Select(genre => new MusicGenreCardDto
            {
                Id = genre.Id,
                Name = genre.Name,
                TrackCount = genre.MusicGenreTracks.Count(),
            })
            .ToListAsync(ct);

        return await MusicGroupCovers.FillGenresAsync(context, userId, genres, ct);
    }

    public Task<List<MusicGenre>> GetPaginatedMusicGenresAsync(
        Guid userId,
        string letter,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        return context
            .MusicGenres.AsNoTracking()
            .Where(genre =>
                genre.AlbumMusicGenres.Any(g =>
                    g.Album.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || genre.ArtistMusicGenres.Any(g =>
                    g.Artist.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Where(genre => genre.MusicGenreTracks.Any())
            .Where(genre =>
                (letter == "_" || letter == "#")
                    ? Letters.Any(p => genre.Name.StartsWith(p.ToLower()))
                    : genre.Name.StartsWith(letter.ToLower())
            )
            .Include(genre => genre.MusicGenreTracks)
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id)
            .Skip(page * take)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<List<MusicGenreCardDto>> GetPaginatedMusicGenreCardsAsync(
        Guid userId,
        string letter,
        int take,
        int page,
        CancellationToken ct = default
    )
    {
        List<MusicGenreCardDto> genres = await context
            .MusicGenres.AsNoTracking()
            .Where(genre =>
                genre.AlbumMusicGenres.Any(g =>
                    g.Album.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || genre.ArtistMusicGenres.Any(g =>
                    g.Artist.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Where(genre => genre.MusicGenreTracks.Any())
            .Where(genre =>
                (letter == "_" || letter == "#")
                    ? Letters.Any(p => genre.Name.StartsWith(p.ToLower()))
                    : genre.Name.StartsWith(letter.ToLower())
            )
            .OrderBy(genre => genre.Name)
            .ThenBy(genre => genre.Id)
            .Skip(page * take)
            .Take(take)
            .Select(genre => new MusicGenreCardDto
            {
                Id = genre.Id,
                Name = genre.Name,
                TrackCount = genre.MusicGenreTracks.Count(),
            })
            .ToListAsync(ct);

        return await MusicGroupCovers.FillGenresAsync(context, userId, genres, ct);
    }

    public async Task<GroupPoster[]> GetMusicGenreCoversAsync(
        Guid userId,
        Guid genreId,
        CancellationToken ct = default
    )
    {
        Dictionary<Guid, GroupPoster[]> covers = await MusicGroupCovers.ForGenresAsync(
            context,
            userId,
            [genreId],
            ct
        );
        return covers.GetValueOrDefault(genreId) ?? [];
    }

    public Task<MusicGenre?> GetMusicGenreAsync(
        Guid userId,
        Guid genreId,
        CancellationToken ct = default
    )
    {
        return context
            .MusicGenres.AsNoTracking()
            .Where(genre =>
                genre.AlbumMusicGenres.Any(g =>
                    g.Album.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
                || genre.ArtistMusicGenres.Any(g =>
                    g.Artist.Library.LibraryUsers.Any(u => u.UserId == userId)
                )
            )
            .Where(genre => genre.Id == genreId)
            .Where(genre => genre.MusicGenreTracks.Any())
            .Include(genre => genre.MusicGenreTracks)
                .ThenInclude(mgt => mgt.Track)
                    .ThenInclude(track => track.TrackUser.Where(tu => tu.UserId == userId))
            .Include(genre => genre.MusicGenreTracks)
                .ThenInclude(mgt => mgt.Track)
                    .ThenInclude(track => track.ArtistTrack)
                        .ThenInclude(at => at.Artist)
                            .ThenInclude(artist => artist.Translations)
            .Include(genre => genre.MusicGenreTracks)
                .ThenInclude(mgt => mgt.Track)
                    .ThenInclude(track => track.ArtistTrack)
                        .ThenInclude(at => at.Artist)
                            .ThenInclude(artist => artist.Images)
            .Include(genre => genre.MusicGenreTracks)
                .ThenInclude(mgt => mgt.Track)
                    .ThenInclude(track => track.AlbumTrack)
                        .ThenInclude(at => at.Album)
                            .ThenInclude(album => album.Translations)
            .Include(genre => genre.MusicGenreTracks)
                .ThenInclude(mgt => mgt.Track)
                    .ThenInclude(track => track.AlbumTrack)
                        .ThenInclude(at => at.Album)
                            .ThenInclude(album => album.AlbumArtist)
                                .ThenInclude(aa => aa.Artist)
                                    .ThenInclude(artist => artist.Images)
            .FirstOrDefaultAsync(ct);
    }
}
