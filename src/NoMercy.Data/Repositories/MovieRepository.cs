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
using Microsoft.Extensions.Logging;
using NoMercy.Data.Extensions;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Users;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Data.Repositories;

public class MovieRepository(
    IDbContextFactory<MediaContext> contextFactory,
    ILogger<MovieRepository> logger
) : IMovieRepository
{
    public async Task<Movie?> GetMovieAsync(
        Guid userId,
        int id,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Movies.AsNoTracking()
            .Where(movie => movie.Id == id)
            .ForUser(userId)
            .Include(movie => movie.MovieUser.Where(mu => mu.UserId == userId))
            .Include(movie => movie.Translations.Where(t => t.Iso6391 == language))
            .Include(movie =>
                movie
                    .Images.Where(i => i.Type == "logo")
                    .OrderByDescending(i => i.VoteAverage)
                    .ThenBy(i => i.Id)
                    .Take(1)
            )
            .Include(movie =>
                movie
                    .CertificationMovies.Where(c =>
                        c.Certification.Iso31661 == "US" || c.Certification.Iso31661 == country
                    )
                    .OrderBy(c => c.CertificationId)
                    .Take(1)
            )
                .ThenInclude(c => c.Certification)
            .Include(movie => movie.VideoFiles.Where(v => v.Folder != null))
                .ThenInclude(v => v.UserData.Where(ud => ud.UserId == userId))
            .FirstOrDefaultAsync(ct);
    }

    private static readonly Func<
        MediaContext,
        Guid,
        int,
        string,
        string,
        Task<Movie?>
    > GetMovieDetailAsyncQuery = EF.CompileAsyncQuery(
        (MediaContext mediaContext, Guid userId, int id, string language, string country) =>
            mediaContext
                .Movies.AsNoTracking()
                .Where(movie => movie.Id == id)
                .Where(tv =>
                    tv.Library.LibraryUsers.FirstOrDefault(u => u.UserId.Equals(userId)) != null
                )
                .Include(movie =>
                    movie.MovieUser.Where(movieUser => movieUser.UserId.Equals(userId))
                )
                .Include(movie => movie.Cast)
                    .ThenInclude(castMovie => castMovie.Person)
                .Include(movie => movie.Cast)
                    .ThenInclude(castMovie => castMovie.Role)
                .Include(movie => movie.Crew)
                    .ThenInclude(crewMovie => crewMovie.Person)
                .Include(movie => movie.Crew)
                    .ThenInclude(crewMovie => crewMovie.Job)
                .Include(movie => movie.Library)
                    .ThenInclude(library => library.LibraryUsers)
                .Include(movie => movie.Media.Where(media => media.Type == "Trailer"))
                .Include(movie => movie.AlternativeTitles)
                .Include(movie =>
                    movie.Translations.Where(translation => translation.Iso6391 == language)
                )
                .Include(movie =>
                    movie
                        .Images.Where(image =>
                            (image.Type == "logo" && image.Iso6391 == "en")
                            || (
                                (image.Type == "backdrop" || image.Type == "poster")
                                && (image.Iso6391 == "en" || image.Iso6391 == null)
                            )
                        )
                        .OrderByDescending(image => image.VoteAverage)
                        .ThenBy(image => image.Id)
                )
                .Include(movie =>
                    movie.CertificationMovies.Where(certification =>
                        certification.Certification.Iso31661 == country
                        || certification.Certification.Iso31661 == "US"
                    )
                )
                    .ThenInclude(certificationMovie => certificationMovie.Certification)
                .Include(movie => movie.GenreMovies)
                    .ThenInclude(genreMovie => genreMovie.Genre)
                .Include(movie => movie.AnimeThemeMovies)
                    .ThenInclude(animeThemeMovie => animeThemeMovie.AnimeTheme)
                .Include(movie => movie.AnimeDemographicMovies)
                    .ThenInclude(animeDemographicMovie => animeDemographicMovie.AnimeDemographic)
                .Include(movie => movie.AnimeSeasonMovies)
                    .ThenInclude(animeSeasonMovie => animeSeasonMovie.AnimeSeason)
                .Include(movie => movie.KeywordMovies)
                    .ThenInclude(keywordMovie => keywordMovie.Keyword)
                .Include(movie => movie.RecommendationFrom)
                .Include(movie => movie.SimilarFrom)
                .Include(movie => movie.VideoFiles)
                    .ThenInclude(file =>
                        file.UserData.Where(userData => userData.UserId.Equals(userId))
                    )
                .Include(movie => movie.WatchProviderMedia.Where(wpm => wpm.CountryCode == country))
                    .ThenInclude(wpm => wpm.WatchProvider)
                .Include(movie => movie.CompaniesMovies)
                    .ThenInclude(ctv => ctv.Company)
                .FirstOrDefault()
    );

    public async Task<Movie?> GetMovieDetailAsync(
        Guid userId,
        int id,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        Movie? movie = await GetMovieDetailAsyncQuery(context, userId, id, language, country);
        if (movie is null)
            return null;

        // A stored relation has no adult flag. Only owned, playable titles that are
        // not adult are offered, and they get the flag from the owned row.
        int[] relatedIds = [.. movie.RecommendationFrom.Select(related => related.MediaId)];
        List<int> ownedIds = await context
            .Movies.AsNoTracking()
            .ForUser(userId)
            .Where(related => relatedIds.Contains(related.Id) && !related.Adult)
            .Where(related => related.VideoFiles.Any(file => file.Folder != null))
            .Select(related => related.Id)
            .ToListAsync(ct);

        movie.RecommendationFrom =
        [
            .. movie.RecommendationFrom.Where(related => ownedIds.Contains(related.MediaId)),
        ];
        foreach (Recommendation related in movie.RecommendationFrom)
            related.Adult = false;

        return movie;
    }

    public async Task<bool> GetMovieAvailableAsync(
        Guid userId,
        int id,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Movies.AsNoTracking()
            .ForUser(userId)
            .Where(movie => movie.Id == id)
            .AnyAsync(movie => movie.VideoFiles.Any(v => v.Folder != null), ct);
    }

    public async Task<List<Movie>> GetMoviePlaylistAsync(
        Guid userId,
        int id,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Movies.AsNoTracking()
            .Where(movie => movie.Id == id)
            .ForUser(userId)
            .Include(movie =>
                movie.Media.Where(media => media.Type == "video" && media.Iso6391 == language)
            )
            .Include(movie =>
                movie.Images.Where(image =>
                    image.Type == "logo" && image.Iso6391 == "en" && image.Width > image.Height
                )
            )
            .Include(movie =>
                movie.Translations.Where(translation => translation.Iso6391 == language)
            )
            .Include(movie => movie.VideoFiles)
                .ThenInclude(videoFile => videoFile.Metadata)
            .Include(movie => movie.VideoFiles)
                .ThenInclude(file =>
                    file.UserData.Where(userData =>
                        userData.UserId.Equals(userId) && userData.Type == MediaTypes.MovieMediaType
                    )
                )
            .Include(movie =>
                movie.CertificationMovies.Where(certification =>
                    certification.Certification.Iso31661 == country
                    || certification.Certification.Iso31661 == "US"
                )
            )
                .ThenInclude(certificationMovie => certificationMovie.Certification)
            .ToListAsync(ct);
    }

    public async Task<bool> LikeMovieAsync(
        int id,
        Guid userId,
        bool like,
        CancellationToken ct = default
    )
    {
        try
        {
            await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
            MovieUser? movieUser = await context.MovieUser.FirstOrDefaultAsync(
                mu => mu.MovieId == id && mu.UserId == userId,
                ct
            );

            if (like)
            {
                await context
                    .MovieUser.Upsert(new(id, userId))
                    .On(m => new { m.MovieId, m.UserId })
                    .WhenMatched(m => new() { MovieId = m.MovieId, UserId = m.UserId })
                    .RunAsync();
            }
            else if (movieUser != null)
            {
                context.MovieUser.Remove(movieUser);
                await context.SaveChangesAsync(ct);
            }

            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
            return false;
        }
    }

    public async Task AddMovieAsync(int id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        Library? movieLibrary = await context
            .Libraries.Where(f => f.Type == MediaTypes.MovieMediaType)
            .FirstOrDefaultAsync(ct);

        if (movieLibrary == null)
            return;

        JobDispatcher jobDispatcher = new();
        jobDispatcher.DispatchJob<MovieImportJob>(id, movieLibrary.Id);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        await MediaSubtreeDelete.MovieAsync(context, id, ct);
    }

    public async Task<bool> AddToWatchListAsync(
        int movieId,
        Guid userId,
        bool add = true,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        Movie? movie = await context
            .Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == movieId, ct);

        if (movie is null)
            return false;

        if (add)
        {
            // Find the movie's video file
            VideoFile? videoFile = await context
                .VideoFiles.Where(vf => vf.MovieId == movieId && vf.Folder != null)
                .FirstOrDefaultAsync(ct);

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
                            MovieId = movieId,
                            Time = 0,
                            LastPlayedDate = DateTime.UtcNow.ToString("o"),
                            Type = MediaTypes.MovieMediaType,
                        }
                    );
                }
            }
        }
        else
        {
            // Remove all userdata for this movie
            List<UserData> userDataToRemove = await context
                .UserData.Where(ud => ud.UserId == userId && ud.MovieId == movieId)
                .ToListAsync(ct);

            context.UserData.RemoveRange(userDataToRemove);
        }

        await context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Movie?> GetMovieForRescanAsync(int id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Movies.AsNoTracking()
            .Include(movie => movie.Library)
                .ThenInclude(library => library.FolderLibraries)
                    .ThenInclude(folderLibrary => folderLibrary.Folder)
            .FirstOrDefaultAsync(movie => movie.Id == id, ct);
    }

    public async Task<Movie?> GetMovieForRefreshAsync(int id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Movies.AsNoTracking()
            .Include(movie => movie.Library)
            .FirstOrDefaultAsync(movie => movie.Id == id, ct);
    }
}
