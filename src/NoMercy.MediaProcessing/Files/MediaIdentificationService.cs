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

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MovieFileLibrary;
using NoMercy.Database;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Episode;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Providers.TMDB.Models.TV;
using NoMercy.Providers.TVDB.Client;
using NoMercy.Providers.TVDB.Models.Episodes;
using NoMercy.Providers.TVDB.Models.Series;
using Serilog.Events;

namespace NoMercy.MediaProcessing.Files;

/// <summary>
/// Resolves a parsed video filename to a TMDB movie/episode match (slice 16.6).
/// Extracted from FileRepository so file enumeration no longer depends on TMDB.
/// A null return means "unidentified" — callers must NOT treat that as "drop".
/// </summary>
public partial class MediaIdentificationService(
    MediaContext context,
    IServiceScopeFactory scopeFactory
) : IMediaIdentificationService
{
    /// <summary>
    /// Runs an import job SYNCHRONOUSLY in a fresh DI scope so constructor-injected
    /// services (IStorageFactory, IStorageDriver, ILoggerFactory) are resolved, then
    /// disposes the scope when Handle() completes. Mirrors QueueWorker.ExecuteWithTransientRetry.
    /// </summary>
    private async Task RunJobSynchronouslyAsync<TJob>(Action<TJob> configure)
        where TJob : AbstractMediaJob
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        TJob job = ActivatorUtilities.CreateInstance<TJob>(scope.ServiceProvider);
        configure(job);
        await job.Handle();
    }

    public async Task<(MovieOrEpisode match, string? imdbId)?> IdentifyAsync(
        MovieFile parsed,
        string libraryType,
        TimeSpan? duration,
        int? overrideTmdbId,
        bool seasonExplicit,
        DateOnly? airDate = null,
        CancellationToken ct = default
    )
    {
        return libraryType switch
        {
            MediaTypes.AnimeMediaType or MediaTypes.TvMediaType => await ResolveShowEpisodeAsync(
                context,
                libraryType,
                parsed,
                duration,
                overrideTmdbId,
                seasonExplicit,
                airDate
            ),
            MediaTypes.MovieMediaType => await ResolveMovieMatchAsync(
                context,
                libraryType,
                parsed,
                duration,
                overrideTmdbId
            ),
            _ => null,
        };
    }

    private async Task<(MovieOrEpisode match, string? imdbId)?> ResolveShowEpisodeAsync(
        MediaContext ctx,
        string libraryType,
        MovieFile parsed,
        TimeSpan? duration,
        int? overrideTmdbId,
        bool seasonExplicit = false,
        DateOnly? airDate = null
    )
    {
        TmdbSearchClient searchClient = new();

        TmdbTvShow? show;
        TmdbPaginatedResponse<TmdbTvShow>? shows = null;

        if (overrideTmdbId.HasValue)
        {
            // Resolve directly by TMDB ID — no text search, no ambiguity
            TmdbTvClient overrideTvClient = new(overrideTmdbId.Value);
            TmdbTvShowDetails? overrideDetails = await overrideTvClient.Details(true);
            if (overrideDetails == null)
                return null;
            show = overrideDetails; // TmdbTvShowDetails : TmdbTvShow
        }
        else
        {
            shows = await searchClient.TvShow(parsed.Title.OrEmpty(), parsed.Year.OrEmpty(), true);
            show = shows?.Results.FirstOrDefault();
        }

        if (show == null)
            return null;

        // A file may have an episode but no season (the season defaults to 1 by FilenameResolver).
        // Episode is required; season will default to 1 if missing. Failure only if episode is null
        // AND there's no air date to fall back on.
        if (parsed.Episode == null && !airDate.HasValue)
            return null;

        // Identification reads. It does not write.
        //
        // This used to import the first TMDB result for whatever a parser read
        // out of a filename, before an episode had been resolved and before
        // anything was confirmed. Listing a folder is what the dashboard does
        // before the owner has committed to anything, and what a plugin asks
        // when it wants to know about a file, so a recurring scan quietly
        // attached thirteen shows nobody had asked for and 1,604 episode rows
        // with them - and nothing anywhere removes a row from LibraryTv.
        //
        // A show the server does not hold is now simply not a match, which the
        // caller already handles. Import stays where it already was: the add-show
        // button, the file watcher, the inbox, and the changes cron all have
        // either a real file or a person behind them.
        bool showIsInLibrary = await ctx.Tvs.AsNoTracking().AnyAsync(item => item.Id == show.Id);

        if (!showIsInLibrary)
            return null;

        // Daily/dated episode (yyyy.mm.dd): map the air date to the episode that
        // aired that day, then fall through to the normal season/episode resolution.
        if (airDate.HasValue && (!parsed.Season.HasValue || !parsed.Episode.HasValue))
        {
            Episode? datedEpisode = await ctx
                .Episodes.AsNoTracking()
                .Where(item =>
                    item.TvId == show.Id
                    && item.AirDate.HasValue
                    && item.AirDate.Value.Year == airDate.Value.Year
                    && item.AirDate.Value.Month == airDate.Value.Month
                    && item.AirDate.Value.Day == airDate.Value.Day
                )
                .OrderBy(item => item.SeasonNumber)
                .ThenBy(item => item.EpisodeNumber)
                .FirstOrDefaultAsync();

            if (datedEpisode == null)
                return null;

            parsed.Season = datedEpisode.SeasonNumber;
            parsed.Episode = datedEpisode.EpisodeNumber;
        }

        // Both Season and Episode may have values, or Episode may exist without Season.
        // Season defaults to 1 if Episode is present but Season is not.
        int seasonNumber = parsed.Season ?? 1;
        int episodeNumber = parsed.Episode!.Value;

        // The file's own embedded title (if the name carries one after the season/episode
        // marker, e.g. "S01E25 - Ah! Urd's Little Romance.mkv") is the one signal that
        // survives a release using its own numbering convention. A release can pack a
        // folder whose flat file numbers don't mean what they mean on TMDB — this show's
        // OWN "\Specials\" folder numbers an unrelated 1993 OVA and a theatrical movie as
        // S00E01-E06, which happen to collide with TMDB's real (and unrelated) S00E01-E06.
        // Without this check, a season/episode NUMBER match was accepted with zero regard
        // for whether the file is actually that content, silently attaching two different
        // physical files to the same DB row because their numbers coincided.
        string? embeddedEpisodeTitle = ExtractEmbeddedEpisodeTitle(parsed.Path);

        // Apply default season if needed for the lookup
        int seasonForLookup = parsed.Season ?? 1;

        Episode? episode = ctx
            .Episodes.AsNoTracking()
            .Where(item => item.TvId == show.Id)
            .Where(item => item.SeasonNumber == seasonForLookup)
            .FirstOrDefault(item => item.EpisodeNumber == parsed.Episode);

        if (episode != null && EmbeddedTitleContradicts(embeddedEpisodeTitle, episode.Title))
            episode = null;

        // When the season was explicit in the filename (e.g. S02E19), try the TMDB API first,
        // then TheTVDB (curated separately from TMDB, and the one that has repeatedly had the
        // correct season/episode split where TMDB's own crowd-edited episode groups did not —
        // "Ah! My Goddess" S00E02/E03 mislabelled S01E25/E26 by two different TMDB groups in a
        // row), and only fall back to TMDB's own groups as the last resort, for shows with no
        // TvdbId. TMDB groups are no longer trusted FIRST just because they answered — a wrong
        // non-null answer from them used to end the search before TVDB was ever consulted.
        if (episode == null && seasonExplicit)
        {
            TmdbEpisodeClient episodeClient = new(show.Id, seasonNumber, episodeNumber);
            TmdbEpisodeDetails? details = await episodeClient.Details(true);

            if (details != null && EmbeddedTitleContradicts(embeddedEpisodeTitle, details.Name))
                details = null;

            if (details == null)
            {
                episode = await ResolveSeasonedEpisodeFromTvdbAsync(
                    ctx,
                    show.Id,
                    seasonNumber,
                    episodeNumber
                );
                if (
                    episode != null
                    && EmbeddedTitleContradicts(embeddedEpisodeTitle, episode.Title)
                )
                    episode = null;
            }

            // Neither TMDB's default season nor TheTVDB had it — TMDB's own episode groups
            // are the last resort, for shows TheTVDB doesn't carry at all.
            if (details == null && episode == null)
            {
                episode = await ResolveSeasonedEpisodeFromGroupsAsync(
                    ctx,
                    show.Id,
                    seasonNumber,
                    episodeNumber
                );
                if (
                    episode != null
                    && EmbeddedTitleContradicts(embeddedEpisodeTitle, episode.Title)
                )
                    episode = null;
            }

            if (details != null && episode == null)
            {
                Season? season = await ctx
                    .Seasons.AsNoTracking()
                    .FirstOrDefaultAsync(s =>
                        s.TvId == show.Id && s.SeasonNumber == details.SeasonNumber
                    );

                episode = new()
                {
                    Id = details.Id,
                    TvId = show.Id,
                    SeasonNumber = details.SeasonNumber,
                    EpisodeNumber = details.EpisodeNumber,
                    Title = details.Name,
                    Overview = details.Overview,
                    Still = details.StillPath,
                    VoteAverage = details.VoteAverage,
                    VoteCount = details.VoteCount,
                    AirDate = details.AirDate,
                    SeasonId = season?.Id ?? 0,
                };

                ctx.Episodes.Add(episode);
                await ctx.SaveChangesAsync();
            }
        }

        // The provider's absolute ordering is the authority on what an absolute number means,
        // so it is asked before anything local is guessed at.
        if (episode == null)
            episode = await ResolveAbsoluteEpisodeAsync(ctx, show.Id, episodeNumber);

        // Last resort: index the show's own episodes as if they were one flat run. Only
        // correct when the provider publishes no absolute ordering and the show really is
        // numbered straight through, so it must never run before the group lookup above —
        // it returns a row for any number within range, which looks like a match and is
        // usually the wrong episode.
        //
        // Never when the filename itself was explicit about the season (seasonExplicit):
        // "Ren s02e18" says season 2, episode 18 — if TMDB's season 2 only has 12 episodes,
        // that is "no such episode", not "reinterpret 18 as a flat cross-season index".
        // Season 1 having exactly 12 episodes of its own put flat position 17 (18 - 1) at
        // season 2's 6th episode, so this silently dispatched an encode of the s02e18 file
        // as "S02E06" and overwrote the real episode 6's output — reproduced live 2026-08-10
        // for Chuunibyou demo Koi ga Shitai! Ren, every s02e14+ file wrapping onto S02E02-E12.
        if (episode == null && !seasonExplicit)
        {
            List<Episode> episodes =
            [
                .. ctx
                    .Episodes.Where(item => item.TvId == show.Id)
                    .Where(item => item.SeasonNumber > 0)
                    .OrderBy(item => item.SeasonNumber)
                    .ThenBy(item => item.EpisodeNumber),
            ];

            episode = episodes.ElementAtOrDefault(episodeNumber - 1);
        }

        // Try alternate search results for absolute-order anime (e.g. TMDB ranks live-action above anime)
        if (episode == null && shows!.Results.Count > 1)
        {
            foreach (TmdbTvShow altShow in shows.Results.Skip(1).Take(4))
            {
                TmdbTvClient altTvClient = new(altShow.Id);
                TmdbTvEpisodeGroups? altGroups = await altTvClient.EpisodeGroups(true);
                if (altGroups?.Results.Any(g => g.Type == 2) != true)
                    continue;

                // Candidates, tested and not attached. This attached every
                // alternative it was unsure about, so one mis-parsed name could
                // pull in five shows and keep the four that did not match.
                episode = await ResolveAbsoluteEpisodeAsync(ctx, altShow.Id, episodeNumber);
                if (episode != null)
                    break;
            }
        }

        // The scene's unpunctuated season+episode: "Show.Name.102" is season one
        // episode two, and "the.event.401" is season four episode one.
        //
        // The same three digits are an absolute episode number in a fansub
        // release — "BLEACH - 154" is the hundred and fifty-fourth episode, not
        // season one's fifty-fourth — and nothing in the NAME tells the two
        // conventions apart. What tells them apart is the show, which the parser
        // does not know and this does. So every absolute reading above is tried
        // first and the split is taken only once they have all failed: only when
        // the whole number cannot be an episode of THIS show is it two numbers.
        //
        // Being last in the ladder is the whole guard. Reading it earlier is how
        // a long-running show's episode 310 becomes its season three episode ten,
        // which is a real episode and therefore a silent, permanent mismatch.
        if (episode == null && !seasonExplicit)
            episode = ResolveSceneSplitEpisode(ctx, show.Id, episodeNumber);

        if (episode == null)
        {
            TmdbEpisodeClient episodeClient = new(show.Id, seasonNumber, episodeNumber);
            TmdbEpisodeDetails? details = await episodeClient.Details(true);
            if (details == null)
                return null;
            if (EmbeddedTitleContradicts(embeddedEpisodeTitle, details.Name))
                return null;

            Season? season = await ctx
                .Seasons.AsNoTracking()
                .FirstOrDefaultAsync(s =>
                    s.TvId == show.Id && s.SeasonNumber == details.SeasonNumber
                );

            episode = new()
            {
                Id = details.Id,
                TvId = show.Id,
                SeasonNumber = details.SeasonNumber,
                EpisodeNumber = details.EpisodeNumber,
                Title = details.Name,
                Overview = details.Overview,
                Still = details.StillPath,
                VoteAverage = details.VoteAverage,
                VoteCount = details.VoteCount,
                AirDate = details.AirDate,
                SeasonId = season?.Id ?? 0,
            };

            ctx.Episodes.Add(episode);
            await ctx.SaveChangesAsync();
        }

        // Prefer the DB row over the search-side TmdbTvShow.Name — the local
        // Tv table is the source of truth for show metadata after a scan, and
        // a freshly added show may have been written by EnsureShowInLibraryAsync
        // above.
        string? showName =
            await ctx
                .Tvs.AsNoTracking()
                .Where(t => t.Id == show.Id)
                .Select(t => t.Title)
                .FirstOrDefaultAsync()
            ?? show.Name;

        MovieOrEpisode match = new()
        {
            Id = episode.Id,
            Title = episode.Title.OrEmpty(),
            ShowName = showName,
            EpisodeNumber = episode.EpisodeNumber,
            SeasonNumber = episode.SeasonNumber,
            Still = episode.Still,
            Duration = duration,
            Overview = episode.Overview,
        };

        return (match, episode.ImdbId);
    }

    /// <summary>
    /// Reads a bare number as the scene's joined season and episode — 102 is
    /// season one episode two, 1109 is season eleven episode nine — and returns
    /// the episode only when the show actually has it.
    /// <para>
    /// The lookup IS the check. A split that names no real episode is not a
    /// weaker match, it is evidence the number was never two numbers, so
    /// nothing is returned and the caller carries on.
    /// </para>
    /// </summary>
    private static Episode? ResolveSceneSplitEpisode(MediaContext ctx, int showId, int number)
    {
        if (number < 100)
            return null;

        int seasonNumber = number / 100;
        int episodeNumber = number % 100;

        if (episodeNumber == 0)
            return null;

        return ctx
            .Episodes.AsNoTracking()
            .Where(item => item.TvId == showId)
            .Where(item => item.SeasonNumber == seasonNumber)
            .FirstOrDefault(item => item.EpisodeNumber == episodeNumber);
    }

    private async Task<(MovieOrEpisode match, string? imdbId)?> ResolveMovieMatchAsync(
        MediaContext ctx,
        string libraryType,
        MovieFile parsed,
        TimeSpan? duration,
        int? overrideTmdbId
    )
    {
        TmdbMovie? movie;

        if (overrideTmdbId.HasValue)
        {
            // Resolve directly by TMDB ID — no text search, no ambiguity
            movie = new() { Id = overrideTmdbId.Value };
        }
        else
        {
            TmdbSearchClient searchClient = new();
            TmdbPaginatedResponse<TmdbMovie>? movies = await searchClient.Movie(
                parsed.Title.OrEmpty(),
                parsed.Year.OrEmpty(),
                true
            );
            movie = movies?.Results.FirstOrDefault();
        }

        if (movie == null)
            return null;

        Movie? movieItem = ctx.Movies.AsNoTracking().FirstOrDefault(item => item.Id == movie.Id);

        if (movieItem == null)
        {
            TmdbMovieClient movieClient = new(movie.Id);
            TmdbMovieDetails? details = await movieClient.Details(true);
            if (details == null)
                return null;

            // The same rule as the show path above, and the same reason. This was
            // only ever reported against shows because shows are what the owner
            // had been scanning; the construction here is identical and imports a
            // whole movie on the first search result for a parsed filename.
            bool hasMovie = ctx.Movies.AsNoTracking().Any(item => item.Id == movie.Id);

            if (!hasMovie)
                return null;

            movieItem = new()
            {
                Id = details.Id,
                Title = details.Title,
                Overview = details.Overview,
                Poster = details.PosterPath,
            };
        }

        MovieOrEpisode match = new()
        {
            Id = movieItem.Id,
            Title = movieItem.Title,
            Still = movieItem.Poster,
            Duration = duration,
            Overview = movieItem.Overview,
        };

        return (match, movieItem.ImdbId);
    }

    private static async Task<Episode?> ResolveAbsoluteEpisodeAsync(
        MediaContext ctx,
        int showId,
        int absoluteEpisodeNumber
    )
    {
        TmdbTvClient tvClient = new(showId);
        TmdbTvEpisodeGroups? episodeGroups = await tvClient.EpisodeGroups(true);
        if (episodeGroups == null)
        {
            Logger.App($"No episode groups found for show {showId}", LogEventLevel.Debug);
            return null;
        }

        // Try all "Absolute" type groups (type 2) — some shows have multiple
        TmdbEpisodeGroupsResult[] absoluteGroups =
        [
            .. episodeGroups.Results.Where(g => g.Type == 2),
        ];

        if (absoluteGroups.Length == 0)
        {
            Logger.App(
                $"No absolute episode group (type 2) for show {showId}, available types: {string.Join(", ", episodeGroups.Results.Select(g => $"{g.Name}={g.Type}"))}",
                LogEventLevel.Debug
            );
            return null;
        }

        foreach (TmdbEpisodeGroupsResult absoluteGroup in absoluteGroups)
        {
            TmdbEpisodeGroupClient groupClient = new(absoluteGroup.Id);
            TmdbEpisodeGroupDetails? groupDetails = await groupClient.Details(true);
            if (groupDetails == null)
            {
                Logger.App(
                    $"Failed to fetch episode group details for {absoluteGroup.Id} ({absoluteGroup.Name})",
                    LogEventLevel.Debug
                );
                continue;
            }

            // Flatten every group into one run, in the order each entry declares — the
            // provider returns a group's episodes in curation order, not sequence.
            //
            // Specials are excluded. An absolute number counts the main run only, but these
            // groups routinely lead with a Specials sub-group, and counting those first
            // shifts every real episode by however many specials exist: with 25 of them in
            // front, absolute 35 resolved to season 1 episode 10.
            List<TmdbEpisodeGroupEpisode> allEpisodes =
            [
                .. groupDetails
                    .Groups.OrderBy(g => g.Order)
                    .SelectMany(g => g.Episodes.OrderBy(episodeInGroup => episodeInGroup.Order))
                    .Where(episodeInGroup => episodeInGroup.SeasonNumber > 0),
            ];

            if (absoluteEpisodeNumber < 1 || absoluteEpisodeNumber > allEpisodes.Count)
            {
                Logger.App(
                    $"Absolute episode {absoluteEpisodeNumber} out of range in '{absoluteGroup.Name}' (has {allEpisodes.Count} episodes)",
                    LogEventLevel.Debug
                );
                continue;
            }

            TmdbEpisodeGroupEpisode target = allEpisodes[absoluteEpisodeNumber - 1];
            Logger.App(
                $"Episode {absoluteEpisodeNumber} counted straight through is "
                    + $"S{target.SeasonNumber:D2}E{target.EpisodeNumber:D2} \"{target.Name}\", "
                    + $"per the show's '{absoluteGroup.Name}' ordering"
            );

            // Look up the resolved episode in the DB
            Episode? episode = await ctx
                .Episodes.AsNoTracking()
                .FirstOrDefaultAsync(e =>
                    e.TvId == showId
                    && e.SeasonNumber == target.SeasonNumber
                    && e.EpisodeNumber == target.EpisodeNumber
                );

            if (episode != null)
                return episode;

            // Fetch from TMDB and add to DB
            TmdbEpisodeClient episodeClient = new(
                showId,
                target.SeasonNumber,
                target.EpisodeNumber
            );
            TmdbEpisodeDetails? details = await episodeClient.Details(true);
            if (details == null)
                continue;

            Season? season = await ctx
                .Seasons.AsNoTracking()
                .FirstOrDefaultAsync(s =>
                    s.TvId == showId && s.SeasonNumber == details.SeasonNumber
                );

            episode = new()
            {
                Id = details.Id,
                TvId = showId,
                SeasonNumber = details.SeasonNumber,
                EpisodeNumber = details.EpisodeNumber,
                Title = details.Name,
                Overview = details.Overview,
                Still = details.StillPath,
                VoteAverage = details.VoteAverage,
                VoteCount = details.VoteCount,
                AirDate = details.AirDate,
                SeasonId = season?.Id ?? 0,
            };

            ctx.Episodes.Add(episode);
            await ctx.SaveChangesAsync();

            return episode;
        }

        return null;
    }

    /// <summary>
    /// Resolves an episode using TMDB episode groups when the default season structure doesn't match.
    /// E.g. Crunchyroll splits a show into S01/S02 but TMDB has it as a single season.
    /// Searches all episode group types for a group whose season/order matches the parsed season,
    /// then finds the episode by number within that group.
    /// </summary>
    private static async Task<Episode?> ResolveSeasonedEpisodeFromGroupsAsync(
        MediaContext ctx,
        int showId,
        int seasonNumber,
        int episodeNumber
    )
    {
        TmdbTvClient tvClient = new(showId);
        TmdbTvEpisodeGroups? episodeGroups = await tvClient.EpisodeGroups(true);
        if (episodeGroups?.Results is not { Length: > 0 })
            return null;

        // Prefer episode groups with the fewest sub-groups that still cover the target season.
        // E.g. for Season 2, a 2-group set (S1+S2) is better than a 3-group set (Specials+S1+S2).
        IEnumerable<TmdbEpisodeGroupsResult> sortedResults = episodeGroups
            .Results.Where(g => g.GroupCount >= seasonNumber)
            .OrderBy(g => g.GroupCount);

        foreach (TmdbEpisodeGroupsResult groupResult in sortedResults)
        {
            TmdbEpisodeGroupClient groupClient = new(groupResult.Id);
            TmdbEpisodeGroupDetails? groupDetails = await groupClient.Details(true);
            if (groupDetails == null)
                continue;

            // A group's sub-groups are not always just "one per real season" — "Ah! My
            // Goddess"'s own "Intended Order" group has THREE: "Special Episodes" (2 eps),
            // "Season 1" (27 eps), "Season 2" (24 eps). Picking sub-group N-1 by position
            // assumed sub-groups are ordered season-1-first with nothing else mixed in; if
            // TMDB's real curation instead sorts "Special Episodes" before the seasons (a
            // real, unverifiable-by-scraping possibility), every single file for the whole
            // show resolved to nothing — not just the edge cases. Selecting by CONTENT
            // (whichever sub-group's episodes are actually dominated by the target season
            // number) is correct regardless of how the provider chose to order them, and a
            // standalone probe against this show's real 3-sub-group data confirmed the old
            // positional approach fails outright under one of the two possible orderings
            // while this one resolves all 50 real local files correctly under either.
            // Season 0 has no sub-group of its own to select this way — every sub-group
            // here carries SOME season-0 entries (the recap inside "Season 1", the trailing
            // specials inside both season sub-groups, plus the dedicated "Special Episodes"
            // sub-group itself), so "most season-0 episodes" would pick one essentially at
            // random. A season-0-explicit file was never in scope for this resolver.
            TmdbEpisodeGroup? targetGroup =
                seasonNumber < 1
                    ? null
                    : groupDetails
                        .Groups.Where(g => g.Episodes.Length > 0)
                        .Select(g => new
                        {
                            Group = g,
                            SameSeasonCount = g.Episodes.Count(e => e.SeasonNumber == seasonNumber),
                        })
                        .Where(g => g.SameSeasonCount > 0)
                        .OrderByDescending(g => g.SameSeasonCount)
                        .Select(g => g.Group)
                        .FirstOrDefault();

            if (targetGroup == null)
                continue;

            // Find the episode by position within this group. Order values are show-global
            // (e.g. 24-47 for Season 2), so use sorted index instead.
            //
            // A "season" group can interleave season-0 specials AMONG its real episodes —
            // TMDB's "Ah! My Goddess" intended-order group inserts a recap special between
            // real episodes 12 and 13 of season 1 — and separately can carry more season-0
            // content AFTER the season's own episodes end ("Ah! Urd's Little Romance" /
            // "Ah! Is My Heart Pounding..." trail season 1's 24 real episodes in the same
            // group). Both are season-0 entries, but they are NOT interchangeable: a file
            // numbered past the season's real count ("25.mkv" in a 24-episode "Season 1"
            // folder) means the release keeps counting past the real episodes into the
            // TRAILING bonus content — never into a special that happened to interrupt the
            // real run partway through. Filtering "every non-matching-season entry" without
            // that distinction put the interleaved recap (season-0, but positioned BEFORE
            // the season finishes) ahead of the real trailing specials, so file 25 resolved
            // to the recap instead of "Urd's Little Romance" — confirmed live against the
            // real 26-file release: episodes 1-24 carry their real titles verbatim (episode
            // 13 is genuinely "Who Does Onee-Sama Belong To", not the recap — this release
            // does not even include the recap as a separate file), so "trailing" must mean
            // AFTER the season's last real episode, not merely "some other season".
            List<TmdbEpisodeGroupEpisode> orderedGroupEpisodes =
            [
                .. targetGroup.Episodes.OrderBy(e => e.Order),
            ];
            List<TmdbEpisodeGroupEpisode> sameSeasonEpisodes =
            [
                .. orderedGroupEpisodes.Where(e => e.SeasonNumber == seasonNumber),
            ];
            int lastSameSeasonIndex = orderedGroupEpisodes.FindLastIndex(e =>
                e.SeasonNumber == seasonNumber
            );
            List<TmdbEpisodeGroupEpisode> trailingExtras =
                lastSameSeasonIndex >= 0
                    ? [.. orderedGroupEpisodes.Skip(lastSameSeasonIndex + 1)]
                    : [];

            // episodeNumber is 1-based and can be 0 or negative for a file the filename
            // parser could not read an episode number out of — ElementAtOrDefault (not
            // the list indexer) is what keeps that a null-and-continue instead of an
            // unhandled ArgumentOutOfRangeException that aborts identification for the
            // whole file, TVDB fallback included, before it ever runs.
            TmdbEpisodeGroupEpisode? target =
                episodeNumber <= sameSeasonEpisodes.Count
                    ? sameSeasonEpisodes.ElementAtOrDefault(episodeNumber - 1)
                    : trailingExtras.ElementAtOrDefault(
                        episodeNumber - sameSeasonEpisodes.Count - 1
                    );

            if (target == null)
                continue;

            Logger.App(
                $"Resolved S{seasonNumber:D2}E{episodeNumber:D2} → TMDB S{target.SeasonNumber:D2}E{target.EpisodeNumber:D2} ({target.Name}) via episode group '{groupResult.Name}'"
            );

            Episode? episode = await LookupOrCreateTmdbEpisodeAsync(
                ctx,
                showId,
                target.SeasonNumber,
                target.EpisodeNumber
            );
            if (episode != null)
                return episode;
        }

        return null;
    }

    /// <summary>
    /// The TVDB fallback for when TMDB has given up entirely — neither its default
    /// season/episode data nor any of its episode groups had this season/episode pair.
    /// TheTVDB is curated separately from TMDB and, for anime specifically, has caught
    /// season/episode splits TMDB's own (crowd-edited) groups get wrong. TVDB only
    /// arbitrates WHICH (season, episode) pair is correct; the row itself is still
    /// built from TMDB's matching episode so the schema stays TMDB-keyed throughout.
    /// </summary>
    private static async Task<Episode?> ResolveSeasonedEpisodeFromTvdbAsync(
        MediaContext ctx,
        int showId,
        int seasonNumber,
        int episodeNumber
    )
    {
        Tv? tv = await ctx.Tvs.AsNoTracking().FirstOrDefaultAsync(t => t.Id == showId);
        if (tv?.TvdbId is not > 0)
        {
            Logger.App(
                $"TheTVDB skipped for show {showId}: no TvdbId on file",
                LogEventLevel.Debug
            );
            return null;
        }

        // "official" buckets every episode by its real season, unlike "dvd" order which
        // can fold bonus/OVA content straight into a season's own numbering with no
        // season-0 split at all — confirmed against "Ah! My Goddess": dvd order runs the
        // two bonus specials as S01E26/E27 with no season-0 boundary, which the DB schema
        // (and every other TVDB/TMDB path here) treats as real season-1 episodes. "official"
        // keeps them in season 0, matching TMDB's own default season data and every other
        // resolution path's expectations.
        TvdbSeriesClient tvdbClient = new(tv.TvdbId.Value);
        TvdbSeriesEpisodesResponse? response = await tvdbClient.Episodes("official");
        if (response?.Data?.Episodes is not { Length: > 0 } tvdbEpisodes)
        {
            // Also the failure shape for a missing/invalid TVDB API key — TvdbBaseClient
            // fails closed (null, zero HTTP calls) rather than throwing, so this is the
            // only place that gap would ever surface.
            Logger.App(
                $"TheTVDB returned no episodes for show {showId} (TvdbId {tv.TvdbId}) — either the series has none under 'official' order, or the TVDB API key isn't configured",
                LogEventLevel.Debug
            );
            return null;
        }

        // Count only the season's own episodes first, then fall through to season 0 once
        // that count is exhausted, at ITS OWN episode numbers. Unlike a TMDB episode
        // group's curated Order field, TVDB's flat per-series list is already bucketed by
        // season with no mid-season interleaving to correct for — sorting by season then
        // number keeps every season-0 entry (interleaved specials included) together as
        // one block, so a plain "not this season" filter is the trailing block here.
        List<TvdbEpisode> orderedTvdbEpisodes =
        [
            .. tvdbEpisodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number),
        ];
        List<TvdbEpisode> sameSeasonTvdbEpisodes =
        [
            .. orderedTvdbEpisodes
                .Where(e => e.SeasonNumber == seasonNumber)
                .OrderBy(e => e.Number),
        ];

        // Same episodeNumber <= 0 guard as the TMDB-group path: the list indexer
        // throws for a negative index, ElementAtOrDefault does not.
        TvdbEpisode? target =
            episodeNumber <= sameSeasonTvdbEpisodes.Count
                ? sameSeasonTvdbEpisodes.ElementAtOrDefault(episodeNumber - 1)
                : orderedTvdbEpisodes
                    .Where(e => e.SeasonNumber != seasonNumber)
                    .ElementAtOrDefault(episodeNumber - sameSeasonTvdbEpisodes.Count - 1);

        if (target == null)
        {
            Logger.App(
                $"TheTVDB has no S{seasonNumber:D2}E{episodeNumber:D2} for show {showId} either — {sameSeasonTvdbEpisodes.Count} season-{seasonNumber} episodes, {orderedTvdbEpisodes.Count(e => e.SeasonNumber != seasonNumber)} elsewhere",
                LogEventLevel.Debug
            );
            return null;
        }

        Logger.App(
            $"Resolved S{seasonNumber:D2}E{episodeNumber:D2} → TMDB S{target.SeasonNumber:D2}E{target.Number:D2} ({target.Name}) via TheTVDB (TMDB had no matching season or episode group)"
        );

        Episode? resolvedEpisode = await LookupOrCreateTmdbEpisodeAsync(
            ctx,
            showId,
            target.SeasonNumber,
            target.Number
        );
        if (resolvedEpisode == null)
            Logger.App(
                $"TheTVDB named S{target.SeasonNumber:D2}E{target.Number:D2} for show {showId}, but TMDB itself has no matching episode there — cannot build a TMDB-keyed row from it",
                LogEventLevel.Debug
            );

        return resolvedEpisode;
    }

    /// <summary>
    /// Fetches an already-known (season, episode) pair from the DB, or creates it from
    /// TMDB's own per-episode endpoint. The episode row is always TMDB-keyed — the group
    /// and TVDB resolvers only ever supply WHICH (season, episode) pair to look up here.
    /// </summary>
    private static async Task<Episode?> LookupOrCreateTmdbEpisodeAsync(
        MediaContext ctx,
        int showId,
        int seasonNumber,
        int episodeNumber
    )
    {
        Episode? episode = await ctx
            .Episodes.AsNoTracking()
            .FirstOrDefaultAsync(e =>
                e.TvId == showId
                && e.SeasonNumber == seasonNumber
                && e.EpisodeNumber == episodeNumber
            );
        if (episode != null)
            return episode;

        TmdbEpisodeClient episodeClient = new(showId, seasonNumber, episodeNumber);
        TmdbEpisodeDetails? details = await episodeClient.Details(true);
        if (details == null)
            return null;

        Season? season = await ctx
            .Seasons.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TvId == showId && s.SeasonNumber == details.SeasonNumber);

        episode = new()
        {
            Id = details.Id,
            TvId = showId,
            SeasonNumber = details.SeasonNumber,
            EpisodeNumber = details.EpisodeNumber,
            Title = details.Name,
            Overview = details.Overview,
            Still = details.StillPath,
            VoteAverage = details.VoteAverage,
            VoteCount = details.VoteCount,
            AirDate = details.AirDate,
            SeasonId = season?.Id ?? 0,
        };

        ctx.Episodes.Add(episode);
        await ctx.SaveChangesAsync();

        return episode;
    }

    /// <summary>
    /// The text after a "SxxExx" marker in a filename, e.g. "Ah! My Goddess - S01E25 - Ah!
    /// Urd's Little Romance.mkv" -&gt; "Ah! Urd's Little Romance". Null when the name carries
    /// no marker or nothing trails it (flat-numbered files stay number-only — there is
    /// nothing to verify a number-based match against).
    /// </summary>
    private static string? ExtractEmbeddedEpisodeTitle(string filePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        Match seasonEpisode = StringExtensions.MatchSeasonEpisode().Match(fileName);
        if (!seasonEpisode.Success)
            return null;

        string tail = fileName[(seasonEpisode.Index + seasonEpisode.Length)..]
            .TrimStart('.', ' ', '-', '_')
            .Trim();

        return string.IsNullOrWhiteSpace(tail) ? null : tail;
    }

    /// <summary>
    /// Whether an embedded episode title rules out a number-based match, by token-overlap
    /// ratio rather than exact/substring comparison — release titles and TMDB's official
    /// titles routinely disagree on translation ("Onee-Sama" vs "Big Sister") without being
    /// different content, so exact matching produced false positives on real, correct
    /// episodes. 0.35 was picked by testing against all 26 real title pairs from this
    /// exact show's season 1 (zero false positives) while still catching genuinely
    /// unrelated content (0.0-0.25 overlap for e.g. "Midsummer Night's Dream" vs "Ah! Urd's
    /// Little Romance" — two different shows' content colliding on the same episode number).
    /// </summary>
    private static bool EmbeddedTitleContradicts(string? embeddedTitle, string? candidateTitle)
    {
        if (string.IsNullOrWhiteSpace(embeddedTitle) || string.IsNullOrWhiteSpace(candidateTitle))
            return false;

        HashSet<string> embeddedTokens = TokenizeTitle(embeddedTitle);
        HashSet<string> candidateTokens = TokenizeTitle(candidateTitle);
        if (embeddedTokens.Count == 0 || candidateTokens.Count == 0)
            return false;

        int commonTokenCount = embeddedTokens.Intersect(candidateTokens).Count();
        double overlapRatio =
            (double)commonTokenCount / Math.Min(embeddedTokens.Count, candidateTokens.Count);

        return overlapRatio < 0.35;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex TitleTokenSeparator();

    private static HashSet<string> TokenizeTitle(string title) =>
        [.. TitleTokenSeparator().Split(title.ToLowerInvariant()).Where(token => token.Length > 0)];
}
