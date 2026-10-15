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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.Data.Repositories;

public class TvShowRepository(
    IDbContextFactory<MediaContext> contextFactory,
    IMediaTypeClassifier mediaTypeClassifier
) : ITvShowRepository
{
    public async Task<TvDetail?> GetTvAsync(
        Guid userId,
        int id,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        // Query 1: Core TV data — show metadata, seasons/episodes, show-level cast/crew, etc.
        // Removed: AlternativeTitles (unused by DTO), Library.LibraryUsers (only needed in WHERE)
        // Episode cast/crew split to Query 2 to reduce round-trips
        Tv? tv = await context
            .Tvs.AsNoTracking()
            .Where(tv => tv.Id == id)
            .ForUser(userId)
            .Include(tv => tv.TvUser)
            .Include(tv => tv.Media.Where(media => media.Type == "Trailer"))
            .Include(tv => tv.Translations.Where(translation => translation.Iso6391 == language))
            .Include(tv =>
                tv.Images.Where(image =>
                        (image.Type == "logo" && image.Iso6391 == "en")
                        || (
                            (image.Type == "backdrop" || image.Type == "poster")
                            && (image.Iso6391 == "en" || image.Iso6391 == null)
                        )
                    )
                    .OrderByDescending(image => image.VoteAverage)
                    .ThenBy(image => image.Id)
            )
            .Include(tv =>
                tv.CertificationTvs.Where(certification =>
                    certification.Certification.Iso31661 == country
                    || certification.Certification.Iso31661 == "US"
                )
            )
                .ThenInclude(certificationTv => certificationTv.Certification)
            .Include(tv => tv.Creators)
                .ThenInclude(genreTv => genreTv.Person)
            .Include(tv => tv.GenreTvs)
                .ThenInclude(genreTv => genreTv.Genre)
            .Include(tv => tv.AnimeThemeTvs)
                .ThenInclude(animeThemeTv => animeThemeTv.AnimeTheme)
            .Include(tv => tv.AnimeDemographicTvs)
                .ThenInclude(animeDemographicTv => animeDemographicTv.AnimeDemographic)
            .Include(tv => tv.AnimeSeasonTvs)
                .ThenInclude(animeSeasonTv => animeSeasonTv.AnimeSeason)
            .Include(tv => tv.KeywordTvs)
                .ThenInclude(keywordTv => keywordTv.Keyword)
            .Include(tv => tv.Cast)
                .ThenInclude(castTv => castTv.Person)
            .Include(tv => tv.Cast)
                .ThenInclude(castTv => castTv.Role)
            .Include(tv => tv.Crew)
                .ThenInclude(crewTv => crewTv.Person)
            .Include(tv => tv.Crew)
                .ThenInclude(crewTv => crewTv.Job)
            .Include(tv => tv.Seasons)
                .ThenInclude(season =>
                    season.Translations.Where(translation => translation.Iso6391 == language)
                )
            .Include(tv => tv.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(episode =>
                        episode.Translations.Where(translation => translation.Iso6391 == language)
                    )
            .Include(tv => tv.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(episode => episode.VideoFiles)
                        .ThenInclude(file =>
                            file.UserData.Where(userData => userData.UserId.Equals(userId))
                        )
            .Include(tv => tv.Episodes)
                .ThenInclude(episode => episode.VideoFiles)
                    .ThenInclude(file =>
                        file.UserData.Where(userData => userData.UserId.Equals(userId))
                    )
            .Include(tv => tv.RecommendationFrom)
            .Include(tv => tv.SimilarFrom)
            .Include(tv => tv.WatchProviderMedia.Where(wpm => wpm.CountryCode == country))
                .ThenInclude(wpm => wpm.WatchProvider)
            .Include(tv => tv.NetworkTvs)
                .ThenInclude(ntv => ntv.Network)
            .Include(tv => tv.CompaniesTvs)
                .ThenInclude(ctv => ctv.Company)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);

        if (tv is null)
            return null;

        // Query 2: Episode-level cast/crew — loaded separately to reduce query complexity
        // This avoids 4 additional split-query round-trips in the main query
        List<Episode> episodesWithCastCrew = await context
            .Episodes.AsNoTracking()
            .Where(e => e.TvId == id)
            .Include(e => e.Cast)
                .ThenInclude(c => c.Person)
            .Include(e => e.Cast)
                .ThenInclude(c => c.Role)
            .Include(e => e.Crew)
                .ThenInclude(c => c.Person)
            .Include(e => e.Crew)
                .ThenInclude(c => c.Job)
            .AsSplitQuery()
            .ToListAsync(ct);

        // Merge episode cast/crew into the main query results
        Dictionary<int, Episode> episodeLookup = episodesWithCastCrew.ToDictionary(e => e.Id);
        foreach (Episode episode in tv.Episodes)
        {
            if (episodeLookup.TryGetValue(episode.Id, out Episode? loaded))
            {
                episode.Cast = loaded.Cast;
                episode.Crew = loaded.Crew;
            }
        }

        foreach (Season season in tv.Seasons)
        {
            foreach (Episode episode in season.Episodes)
            {
                if (episodeLookup.TryGetValue(episode.Id, out Episode? loaded))
                {
                    episode.Cast = loaded.Cast;
                    episode.Crew = loaded.Crew;
                }
            }
        }

        // Related shows (similar / recommended) enriched with availability, so the
        // DTO can render related cards without itself touching a DbContext.
        int[] similarIds = tv.SimilarFrom.Select(similar => similar.MediaId).ToArray();
        Tv[] similars = await context
            .Tvs.AsNoTracking()
            .Where(t => similarIds.Contains(t.Id))
            .Include(t => t.Episodes)
                .ThenInclude(episode => episode.VideoFiles)
            .ToArrayAsync(ct);

        int[] recommendationIds = tv
            .RecommendationFrom.Select(recommendation => recommendation.MediaId)
            .ToArray();
        Tv[] recommendations = await context
            .Tvs.AsNoTracking()
            .Where(t => recommendationIds.Contains(t.Id))
            .Include(t => t.Episodes)
                .ThenInclude(episode => episode.VideoFiles)
            .ToArrayAsync(ct);

        return new(tv, similars, recommendations);
    }

    public async Task<Tv?> GetTvWithLibraryAsync(int id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Tvs.AsNoTracking()
            .Include(tv => tv.Library)
                .ThenInclude(library => library.FolderLibraries)
                    .ThenInclude(folderLibrary => folderLibrary.Folder)
            .FirstOrDefaultAsync(tv => tv.Id == id, ct);
    }

    public async Task<bool> GetTvAvailableAsync(Guid userId, int id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Tvs.AsNoTracking()
            .ForUser(userId)
            .Where(tv => tv.Id == id)
            .AnyAsync(
                tv =>
                    tv.Episodes.Any(e =>
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
                ct
            );
    }

    public async Task<Tv?> GetPlaylistAsync(
        Guid userId,
        int id,
        string language,
        string country,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        return await context
            .Tvs.AsNoTracking()
            .Where(tv => tv.Id == id)
            .ForUser(userId)
            .Include(tv =>
                tv.Seasons.OrderBy(season => season.SeasonNumber).ThenBy(season => season.Id)
            )
                .ThenInclude(season =>
                    season
                        .Episodes.OrderBy(episode => episode.EpisodeNumber)
                        .ThenBy(episode => episode.Id)
                )
            .Include(tv => tv.Translations.Where(translation => translation.Iso6391 == language))
            .Include(tv => tv.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(tv => tv.Tv)
                        .ThenInclude(tv =>
                            tv.Translations.Where(translation => translation.Iso6391 == language)
                        )
            .Include(tv => tv.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(tv => tv.Tv)
                        .ThenInclude(tv => tv.Media.Where(media => media.Type == "video"))
            .Include(tv => tv.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(tv => tv.Tv)
                        .ThenInclude(tv =>
                            tv.Images.Where(image =>
                                image.Type == "logo"
                                && image.Iso6391 == "en"
                                && image.Width > image.Height
                            )
                        )
            .Include(tv => tv.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(tv => tv.VideoFiles)
                        .ThenInclude(videoFile => videoFile.Metadata)
            .Include(tv => tv.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(tv => tv.VideoFiles)
                        .ThenInclude(file =>
                            file.UserData.Where(userData =>
                                userData.UserId.Equals(userId) && userData.Type == "tv"
                            )
                        )
            .Include(tv => tv.Seasons)
                .ThenInclude(season =>
                    season.Translations.Where(translation => translation.Iso6391 == language)
                )
            .Include(tv => tv.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(episode =>
                        episode.Translations.Where(translation => translation.Iso6391 == language)
                    )
            .Include(tv => tv.Seasons)
                .ThenInclude(season => season.Episodes)
                    .ThenInclude(tv => tv.Tv)
                        .ThenInclude(tv =>
                            tv.CertificationTvs.Where(certification =>
                                certification.Certification.Iso31661 == country
                                || certification.Certification.Iso31661 == "US"
                            )
                        )
                            .ThenInclude(certificationTv => certificationTv.Certification)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> LikeAsync(
        int id,
        Guid userId,
        bool like,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        TvUser? tvUser = await context.TvUser.FirstOrDefaultAsync(
            tu => tu.TvId == id && tu.UserId == userId,
            ct
        );

        if (like)
        {
            await context
                .TvUser.Upsert(new(id, userId))
                .On(m => new { m.TvId, m.UserId })
                .WhenMatched(m => new() { TvId = m.TvId, UserId = m.UserId })
                .RunAsync();
        }
        else if (tvUser != null)
        {
            context.TvUser.Remove(tvUser);
            await context.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task AddTvShowAsync(int id)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync();
        TmdbTvClient tvClient = new(id);
        TmdbTvShowDetails? show = await tvClient.Details(true);
        if (show == null)
            return;

        string? mediaType = await mediaTypeClassifier.ClassifyAsync(
            show.Name,
            show.FirstAirDate.ParseYear(),
            show.OriginCountry
        );

        // mediaType is null when the classifier lookup was inconclusive — skip the
        // type-specific match entirely rather than querying for a library of type
        // null, and fall through to the same "tv" default used when no library of
        // the classified type exists.
        Library? tvLibrary =
            (
                mediaType is not null
                    ? await context.Libraries.Where(f => f.Type == mediaType).FirstOrDefaultAsync()
                    : null
            ) ?? await context.Libraries.Where(f => f.Type == "tv").FirstOrDefaultAsync();

        if (tvLibrary == null)
            return;

        JobDispatcher jobDispatcher = new();
        jobDispatcher.DispatchJob(
            new ShowImportJob
            {
                Id = id,
                LibraryId = tvLibrary.Id,
                AddedBy = LibraryLinkOrigin.Manual,
            }
        );
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        await MediaSubtreeDelete.ShowAsync(context, id, ct);
    }

    public async Task<IEnumerable<Episode>> GetMissingLibraryShows(
        Guid userId,
        int id,
        string language,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        Tv? tv = await context
            .Tvs.AsNoTracking()
            .Where(tv => tv.Id == id)
            .ForUser(userId)
            .Include(tv => tv.Episodes.Where(e => !e.VideoFiles.Any()))
                .ThenInclude(e => e.Translations.Where(t => t.Iso6391 == language))
            .FirstOrDefaultAsync(ct);

        return tv?.Episodes ?? [];
    }

    public async Task<bool> AddToWatchListAsync(
        int tvId,
        Guid userId,
        bool add = true,
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
        Tv? tv = await context.Tvs.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tvId, ct);

        if (tv is null)
            return false;

        if (add)
        {
            // Find season 1, episode 1 with its video file
            Episode? season1Episode1 = await context
                .Episodes.Include(e => e.VideoFiles)
                .FirstOrDefaultAsync(
                    e => e.TvId == tvId && e.SeasonNumber == 1 && e.EpisodeNumber == 1,
                    ct
                );

            if (season1Episode1 is not null && season1Episode1.VideoFiles.Any())
            {
                VideoFile videoFile = season1Episode1.VideoFiles.Newest()!;

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
                            TvId = tvId,
                            Time = 0,
                            LastPlayedDate = DateTime.UtcNow.ToString("o"),
                            Type = "tv",
                        }
                    );
                }
            }
        }
        else
        {
            // Remove all userdata for this tv show
            List<UserData> userDataToRemove = await context
                .UserData.Where(ud => ud.UserId == userId && ud.TvId == tvId)
                .ToListAsync(ct);

            context.UserData.RemoveRange(userDataToRemove);
        }

        await context.SaveChangesAsync(ct);
        return true;
    }
}
