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

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs.Dto;
using NoMercy.NmSystem;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.FFProbe;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.AcoustId;
using NoMercy.Providers.AcoustId.Client;
using NoMercy.Providers.AcoustId.Models;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.MediaProcessing.Files;

public class FileRepository(MediaContext context, IStorageDriver storageDriver) : IFileRepository
{
    public IStorageDriver StorageDriver => storageDriver;

    public Task<IDbContextTransaction> BeginTransactionAsync()
    {
        return context.Database.BeginTransactionAsync();
    }

    public async Task StoreVideoFile(VideoFile videoFile)
    {
        VideoFile? existing = await context.VideoFiles.FirstOrDefaultAsync(v =>
            v.Filename == videoFile.Filename && v.HostFolder == videoFile.HostFolder
        );

        if (existing is null)
        {
            context.VideoFiles.Add(videoFile);
            await context.SaveChangesAsync();
            Logger.App(
                $"[StoreVideoFile] inserted {videoFile.Filename}",
                LogEventLevel.Information
            );
            return;
        }

        existing.EpisodeId = videoFile.EpisodeId;
        existing.LastEpisodeNumber = videoFile.LastEpisodeNumber;
        existing.MovieId = videoFile.MovieId;
        existing.Folder = videoFile.Folder;
        existing.HostFolder = videoFile.HostFolder;
        existing.Filename = videoFile.Filename;
        existing.Share = videoFile.Share;
        existing.Duration = videoFile.Duration;
        existing.Chapters = videoFile.Chapters;
        existing.Languages = videoFile.Languages;
        existing.Quality = videoFile.Quality;
        existing.Subtitles = videoFile.Subtitles;
        existing._tracks = videoFile._tracks;
        existing.MetadataId = videoFile.MetadataId;

        await context.SaveChangesAsync();
        Logger.App($"[StoreVideoFile] updated {videoFile.Filename}", LogEventLevel.Information);
    }

    public async Task<Ulid> StoreMetadata(Metadata metadata)
    {
        Metadata? existing = await context.Metadata.FirstOrDefaultAsync(m =>
            m.Filename == metadata.Filename && m.HostFolder == metadata.HostFolder
        );

        if (existing is null)
        {
            context.Metadata.Add(metadata);
            await context.SaveChangesAsync();
            Logger.App(
                $"[StoreMetadata] inserted {metadata.Filename} (id={metadata.Id})",
                LogEventLevel.Information
            );
            return metadata.Id;
        }

        existing.AudioTrackId = metadata.AudioTrackId;
        existing.Duration = metadata.Duration;
        existing.Filename = metadata.Filename;
        existing.Folder = metadata.Folder;
        existing.FolderSize = metadata.FolderSize;
        existing.HostFolder = metadata.HostFolder;
        existing.Type = metadata.Type;
        existing._audio = metadata._audio;
        existing._chapters = metadata._chapters;
        existing._chapters_file = metadata._chapters_file;
        existing._fonts = metadata._fonts;
        existing._fonts_file = metadata._fonts_file;
        existing._previews = metadata._previews;
        existing._subtitles = metadata._subtitles;
        existing._video = metadata._video;

        await context.SaveChangesAsync();
        Logger.App(
            $"[StoreMetadata] updated {metadata.Filename} (id={existing.Id})",
            LogEventLevel.Information
        );
        return existing.Id;
    }

    public async Task<Episode?> GetEpisode(int? showId, MediaFile item)
    {
        if (item.Parsed == null)
            return null;

        return await context
            .Episodes.Where(e => e.TvId == showId)
            .Where(e => e.SeasonNumber == item.Parsed!.Season)
            .Where(e => e.EpisodeNumber == item.Parsed!.Episode)
            .FirstOrDefaultAsync();
    }

    public async Task<Episode?> GetEpisodeById(int episodeId)
    {
        return await context.Episodes.FirstOrDefaultAsync(e => e.Id == episodeId);
    }

    public async Task RecordUnmatchedEpisodeFileAsync(
        string filePath,
        Ulid libraryId,
        string reason
    )
    {
        await ImportFailureRecorder.RecordAsync(
            context,
            "EpisodeFileMatch",
            filePath,
            libraryId,
            reason
        );
    }

    public async Task<(Movie? movie, Tv? show, string type)> MediaType(int id, Library library)
    {
        Movie? movie = null;
        Tv? show = null;
        string type = "";

        switch (library.Type)
        {
            case MediaTypes.MovieMediaType:
                movie = await context
                    .Movies.IgnoreQueryFilters()
                    .Where(m => m.Id == id)
                    .FirstOrDefaultAsync();
                type = library.Type;
                break;
            case MediaTypes.TvMediaType:
            case MediaTypes.AnimeMediaType:
                show = await context.Tvs.Where(t => t.Id == id).FirstOrDefaultAsync();

                if (show == null)
                {
                    Episode? episode = await context
                        .Episodes.Where(e => e.Id == id)
                        .FirstOrDefaultAsync();

                    if (episode != null)
                    {
                        show = await context
                            .Tvs.Where(t => t.Id == episode.TvId)
                            .FirstOrDefaultAsync();
                    }
                }

                type = library.Type;
                break;
        }

        return (movie, show, type);
    }

    /// <summary>
    /// Extracts the underlying <see cref="IStorageDriver"/> from an <see cref="IStorage"/>
    /// instance. Used by callers (e.g. <see cref="MediaScan"/>) that accept the lower-level
    /// interface directly.
    /// </summary>
    public static IStorageDriver StorageDriverFromStorage(IStorage storage) => storage.Driver;

    // Tracks recent CoverArt search queries to avoid duplicate lookups within a scan.
    private static readonly List<string> PrevSearchQueries = [];

    // AcoustID answers a well-known track with every pressing and compilation it
    // appears on — a few hundred releases per file. Each one costs a rate-limited
    // MusicBrainz call and then an ffprobe of the whole folder to score it, so the
    // candidates are ranked first and only the plausible ones are fetched.
    private const int MaxFingerprintReleaseLookups = 10;

    // Test-only visibility (NoMercy.Tests.MediaProcessing has InternalsVisibleTo) so the
    // ranking can be proven without a fingerprinter and a live AcoustID lookup.
    internal readonly record struct ReleaseCandidate(
        Guid Id,
        int? TrackCount,
        string? Title,
        string? Artist,
        int? Year
    );

    /// <summary>
    /// What the folder's own tags claim to be, agreed across its tracks. A folder can hold
    /// one stray mistagged file, so each field is the value most of the tracks carry.
    /// </summary>
    internal readonly record struct FolderTags(string? Album, string? Artist, int? Year);

    public static async Task<List<FileItem>> GetMusicBrainzReleasesInDirectory(
        string folder,
        IStorageDriver storageDriver,
        IAudioFingerprinter audioFingerprinter
    )
    {
        PrevSearchQueries.Clear();

        MediaScan mediaScan = new(storageDriver);
        ConcurrentBag<MediaFolderExtend> mediaFolders = await mediaScan
            .EnableFileListing()
            .FilterByMediaType("music")
            .Process(folder, 2);

        if (mediaFolders.Count == 0)
            return [];

        ConcurrentBag<MediaFile> mediaFiles = mediaFolders
            .SelectMany(m => m.Files ?? [])
            .ToConcurrentBag();

        using MusicBrainzReleaseClient musicBrainzReleaseClient = new();
        using MusicBrainzRecordingClient musicBrainzRecordingClient = new();
        List<Guid> lookupReleaseIds = [];

        (List<MusicBrainzReleaseAppends> releases, string year) =
            await SearchForReleasesFromMediaFiles(
                mediaFiles,
                musicBrainzReleaseClient,
                lookupReleaseIds,
                musicBrainzRecordingClient,
                audioFingerprinter
            );

        releases = await FetchReleaseAppends(lookupReleaseIds, musicBrainzReleaseClient, releases);
        List<FileItem> files = await GenerateResponse(folder, releases, mediaFiles, year);
        return files;
    }

    private static async Task<(
        List<MusicBrainzReleaseAppends> releases,
        string year
    )> SearchForReleasesFromMediaFiles(
        ConcurrentBag<MediaFile> mediaFiles,
        MusicBrainzReleaseClient musicBrainzReleaseClient,
        List<Guid> lookupReleaseIds,
        MusicBrainzRecordingClient musicBrainzRecordingClient,
        IAudioFingerprinter audioFingerprinter
    )
    {
        string prevMusicBrainzReleaseId = string.Empty;
        string year = "0";
        List<MusicBrainzReleaseAppends> releases = [];
        object lockObject = new();
        ConcurrentBag<ReleaseCandidate> fingerprintCandidates = [];
        ConcurrentBag<AudioTagModel> tagModels = [];

        await Parallel.ForEachAsync(
            mediaFiles,
            SystemParallelism.Options,
            async (mediaFile, _) =>
            {
                AudioTagModel audioTagModel = await AudioTagModel.Create(mediaFile);

                if (audioTagModel.Tags == null)
                    return;

                tagModels.Add(audioTagModel);
                if (!string.IsNullOrEmpty(audioTagModel.Tags.MusicBrainzReleaseId))
                {
                    (prevMusicBrainzReleaseId, year) = await FromMusicBrainzRelease(
                        musicBrainzReleaseClient,
                        audioTagModel,
                        lockObject,
                        releases,
                        prevMusicBrainzReleaseId,
                        year
                    );
                }
                else
                {
                    foreach (
                        ReleaseCandidate candidate in await FromFingerprint(
                            mediaFile,
                            audioFingerprinter
                        )
                    )
                        fingerprintCandidates.Add(candidate);
                }
            }
        );

        foreach (
            Guid releaseId in RankCandidates(
                fingerprintCandidates,
                mediaFiles.Count,
                SummariseTags(tagModels)
            )
        )
        {
            MusicBrainzReleaseAppends? release = await musicBrainzReleaseClient.WithAllAppends(
                releaseId
            );
            if (release == null || release.Id == Guid.Empty)
                continue;
            releases.Add(release);
        }

        releases = [.. releases.Where(x => x.Id != Guid.Empty).DistinctBy(x => x.Id)];
        return (releases, year);
    }

    /// <summary>
    /// Picks the releases worth fetching from the ones AcoustID named, scoring each on
    /// every signal available before a single MusicBrainz call is spent.
    /// <para>
    /// Agreement alone is not enough: a well-known track names hundreds of compilations,
    /// and the ones carrying it most often are the ones this folder is least likely to
    /// be. Matching the folder's own tags is what separates the album from the
    /// compilations that merely contain its songs, so a tagged folder ranks on its album,
    /// artist and year even when no MusicBrainz id was ever embedded in it.
    /// </para>
    /// <para>
    /// Nothing here is a filter. A folder missing a track, holding a bonus disc, or
    /// tagged wrongly still returns its best guesses to triage — "no results" is the
    /// failure this whole path exists to stop.
    /// </para>
    /// </summary>
    internal static IEnumerable<Guid> RankCandidates(
        IEnumerable<ReleaseCandidate> candidates,
        int fileCount,
        FolderTags folderTags = default
    )
    {
        List<IGrouping<Guid, ReleaseCandidate>> grouped =
        [
            .. candidates.GroupBy(candidate => candidate.Id),
        ];

        int mostVotes = grouped.Count == 0 ? 0 : grouped.Max(group => group.Count());

        return grouped
            .OrderByDescending(group => ScoreCandidate(group, fileCount, folderTags, mostVotes))
            .ThenByDescending(group => group.Count())
            .Take(MaxFingerprintReleaseLookups)
            .Select(group => group.Key);
    }

    private static double ScoreCandidate(
        IGrouping<Guid, ReleaseCandidate> group,
        int fileCount,
        FolderTags folderTags,
        int mostVotes
    )
    {
        ReleaseCandidate candidate = group.First();
        double score = mostVotes == 0 ? 0 : (double)group.Count() / mostVotes;

        if (group.Any(release => release.TrackCount == fileCount))
            score += 2;

        if (TitlesAgree(folderTags.Album, candidate.Title))
            score += 3;

        if (TitlesAgree(folderTags.Artist, candidate.Artist))
            score += 1.5;

        if (folderTags.Year is > 0 && folderTags.Year == candidate.Year)
            score += 1;

        return score;
    }

    private static bool TitlesAgree(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return left.ContainsSanitized(right) || right.ContainsSanitized(left);
    }

    /// <summary>
    /// The album the folder claims to be, taken from the value most of its tracks agree
    /// on so one mistagged file cannot rename the folder.
    /// </summary>
    private static FolderTags SummariseTags(IEnumerable<AudioTagModel> tagModels)
    {
        List<TagLib.Tag> tags =
        [
            .. tagModels
                .Select(model => model.Tags)
                .Where(tag => tag is not null)
                .Select(tag => tag!),
        ];

        return new(
            MostCommon(tags.Select(tag => tag.Album)),
            MostCommon(tags.Select(tag => tag.AlbumArtists.FirstOrDefault() ?? tag.FirstPerformer)),
            tags.Select(tag => (int)tag.Year)
                .Where(year => year > 0)
                .GroupBy(year => year)
                .OrderByDescending(group => group.Count())
                .Select(group => (int?)group.Key)
                .FirstOrDefault()
        );
    }

    private static string? MostCommon(IEnumerable<string?> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();

    /// <summary>
    /// Names the releases AcoustID associates with one track. Deliberately does no
    /// MusicBrainz lookups: a single track can point at hundreds of releases, and which
    /// of them to fetch is only decidable once every track in the folder has voted.
    /// One release is counted once per track, so the tally means "how many tracks agree".
    /// </summary>
    private static async Task<List<ReleaseCandidate>> FromFingerprint(
        MediaFile mediaFile,
        IAudioFingerprinter audioFingerprinter
    )
    {
        using AcoustIdFingerprintClient acoustIdFingerprintClient = new(audioFingerprinter);
        AcoustIdFingerprint? acoustIds = await acoustIdFingerprintClient.Lookup(mediaFile.Path);
        if (acoustIds == null)
            return [];

        return
        [
            .. acoustIds
                .Results.SelectMany(fingerPrint => fingerPrint.Recordings ?? [])
                .SelectMany(recording => recording?.Releases ?? [])
                .Where(release => release.Id != Guid.Empty)
                .Select(release => new ReleaseCandidate(
                    release.Id,
                    release.TrackCount,
                    release.Title,
                    release.Artists.FirstOrDefault()?.Name,
                    release.Date?.Year
                ))
                .DistinctBy(candidate => candidate.Id),
        ];
    }

    private static async Task<(
        string prevMusicBrainzReleaseId,
        string year
    )> FromMusicBrainzRelease(
        MusicBrainzReleaseClient musicBrainzReleaseClient,
        AudioTagModel audioTagModel,
        object lockObject,
        List<MusicBrainzReleaseAppends> releases,
        string prevMusicBrainzReleaseId,
        string year
    )
    {
        if (prevMusicBrainzReleaseId == audioTagModel.Tags?.MusicBrainzReleaseId)
        {
            if (year == "0")
                year = audioTagModel.Tags?.Year.ToString() ?? "0";
            return (prevMusicBrainzReleaseId, year);
        }

        Guid musicBrainzReleaseId = Guid.Parse(
            (audioTagModel.Tags?.MusicBrainzReleaseId).OrEmpty()
        );
        if (musicBrainzReleaseId == Guid.Empty)
            return (prevMusicBrainzReleaseId, year);
        MusicBrainzReleaseAppends? release = await musicBrainzReleaseClient.WithAllAppends(
            musicBrainzReleaseId
        );

        if (release == null || release.Id == Guid.Empty)
            return (prevMusicBrainzReleaseId, year);
        prevMusicBrainzReleaseId = release.Id.ToString();
        lock (lockObject)
        {
            releases.Add(release);
        }

        return (prevMusicBrainzReleaseId, year);
    }

    private static async Task<List<FileItem>> GenerateResponse(
        string folder,
        List<MusicBrainzReleaseAppends> releases,
        ConcurrentBag<MediaFile> mediaFiles,
        string year
    )
    {
        if (releases.Count == 0)
            return [];

        List<FileItem> files = [];

        MusicBrainzReleaseAppends? bestResult = await GetBestMatchedRelease(mediaFiles, releases);
        if (bestResult != null)
        {
            Logger.MusicBrainz(
                $"Best match: {bestResult.Title} - {bestResult.Id}",
                LogEventLevel.Verbose
            );

            Uri? coverPaletteUrl = await CoverArtImageManagerManager.GetCoverUrl(
                bestResult.Id,
                true
            );

            files.Add(
                new()
                {
                    Size = mediaFiles.Sum(x => x.Size),
                    Mode = 0,
                    Name = bestResult.Title,
                    Parent = folder,
                    Parsed = new(folder)
                    {
                        Title = bestResult.Title,
                        Year = bestResult.DateTime?.Year.ToString() ?? year,
                        IsSeries = false,
                        IsSuccess = true,
                    },
                    Match = new()
                    {
                        Id = bestResult.Id,
                        Title = bestResult.Title,
                        Still = coverPaletteUrl?.ToString(),
                    },
                    Path = folder,
                    Tracks = bestResult.Media.Sum(m => m.TrackCount),
                    Streams = new()
                    {
                        Audio =
                        [
                            new()
                            {
                                Index = 0,
                                Language =
                                    $"Best Match {string.Join(", ", bestResult.Media.Select<MusicBrainzMedia, string>(m => m.Format))}",
                            },
                        ],
                    },
                }
            );
        }

        await Parallel.ForEachAsync(
            releases,
            SystemParallelism.Options,
            async (release, _) =>
            {
                if (files.Any(x => x.Match.Id == release.Id))
                    return;

                Uri? coverPaletteUrl = await CoverArtImageManagerManager.GetCoverUrl(
                    release.Id,
                    true
                );

                files.Add(
                    new()
                    {
                        Size = mediaFiles.Sum(x => x.Size),
                        Mode = 0,
                        Name = release.Title,
                        Parent = folder,
                        Parsed = new(folder)
                        {
                            Title = release.Title,
                            Year = release.DateTime?.Year.ToString() ?? year,
                            IsSeries = false,
                            IsSuccess = true,
                        },
                        Match = new()
                        {
                            Id = release.Id,
                            Title = release.Title,
                            Still = coverPaletteUrl?.ToString(),
                        },
                        Path = folder,
                        Tracks = release.Media.Sum(m => m.TrackCount),
                        Streams = new()
                        {
                            Audio =
                            [
                                new()
                                {
                                    Index = 0,
                                    Language =
                                        $"Formats: {string.Join(", ", release.Media.Select(m => m.Format))}",
                                },
                            ],
                        },
                    }
                );
            }
        );

        return files;
    }

    private static async Task<List<MusicBrainzReleaseAppends>> FetchReleaseAppends(
        List<Guid> lookupReleaseIds,
        MusicBrainzReleaseClient musicBrainzReleaseClient,
        List<MusicBrainzReleaseAppends> releases
    )
    {
        object lockObject = new();
        lookupReleaseIds = [.. lookupReleaseIds.DistinctBy(x => x)];
        await Parallel.ForEachAsync(
            lookupReleaseIds,
            SystemParallelism.Options,
            async (releaseId, _) =>
            {
                MusicBrainzReleaseAppends? musicBrainzRelease =
                    await musicBrainzReleaseClient.WithAllAppends(releaseId, true);
                if (musicBrainzRelease == null || releases.Any(r => r.Id == musicBrainzRelease.Id))
                    return;
                lock (lockObject)
                {
                    releases.Add(musicBrainzRelease);
                }
            }
        );

        return [.. releases.Where(x => x.Id != Guid.Empty).DistinctBy(x => x.Id)];
    }

    private static async Task<MusicBrainzReleaseAppends?> GetBestMatchedRelease(
        ConcurrentBag<MediaFile> mediaFiles,
        List<MusicBrainzReleaseAppends> matchedReleases
    )
    {
        MusicBrainzReleaseAppends? bestRelease = null;
        int highestScore = 0;
        object lockObject = new();

        await Parallel.ForEachAsync(
            matchedReleases,
            SystemParallelism.Options,
            async (release, cancellationToken) =>
            {
                int score = await CalculateMatchScoreAsync(release, mediaFiles);
                lock (lockObject)
                {
                    if (score <= highestScore)
                        return;
                    highestScore = score;
                    bestRelease = release;
                }
            }
        );

        // Every track in the folder has to land on the release before it is called the
        // match. A partial match is a guess, and this answer names folders on disk — an
        // album the user never confirmed must never be the one that renames their files.
        // Releases below the bar still come back as candidates to choose from; they just
        // do not get to claim they are the album.
        return highestScore == mediaFiles.Count ? bestRelease : null;
    }

    private static async Task<int> CalculateMatchScoreAsync(
        MusicBrainzReleaseAppends release,
        ConcurrentBag<MediaFile> localFiles
    )
    {
        int score = 0;

        if (release.Media.Length == 0)
            return 0;

        await Parallel.ForEachAsync(
            release.Media,
            SystemParallelism.Options,
            async (media, cancellationToken) =>
            {
                if (media.Tracks.Length == 0 || media.TrackCount == 0)
                    return;

                await Parallel.ForEachAsync(
                    localFiles,
                    SystemParallelism.Options,
                    async (file, ct) =>
                    {
                        try
                        {
                            file.TagFile ??= TagFile.Create(file.Path);
                            file.FFprobe ??= await FfProbe.CreateAsync(file.Path, ct);

                            int trackIndex = localFiles.ToList().IndexOf(file);
                            bool isMatch = media.Tracks.Any(track =>
                            {
                                bool nameMatch = CompareTrackName(file, track);
                                bool numberMatch = CompareTrackNumber(file, track, trackIndex);
                                bool durationMatch = CompareTrackDuration(file, track);
                                return nameMatch && numberMatch && durationMatch;
                            });

                            if (!isMatch)
                                return;
                            Interlocked.Increment(ref score);
                        }
                        catch (Exception ex)
                        {
                            // Scoring a file is how a release earns the match, so a file
                            // that throws here silently lowers the score and the album
                            // stops being recognised. A bare ex.Message named neither the
                            // frame nor the release, which made a NullReferenceException
                            // per file unactionable.
                            Logger.MusicBrainz(
                                $"Error scoring {file.Path} against release {release.Id}: {ex}",
                                LogEventLevel.Error
                            );
                        }
                    }
                );
            }
        );

        return score;
    }

    private static bool CompareTrackDuration(MediaFile mediaFile, MusicBrainzTrack track)
    {
        double duration = track.Duration;
        double tagDuration = mediaFile.TagFile?.Properties?.Duration.TotalSeconds ?? 0;
        double fileDuration = mediaFile.FFprobe?.Duration.TotalSeconds ?? 0;

        if (duration == 0 && fileDuration == 0 && tagDuration == 0)
            return false;

        return Math.Abs(duration - fileDuration).ToInt() < 3
            || Math.Abs(duration - tagDuration).ToInt() < 3;
    }

    private static bool CompareTrackNumber(
        MediaFile mediaFile,
        MusicBrainzTrack track,
        int trackIndex
    )
    {
        int trackNumber = track.Position;
        long tagTrackNumber = mediaFile.TagFile?.Tag?.Track ?? 0;
        int fileTrackNumber = mediaFile.Parsed?.TrackNumber ?? 0;

        if (trackNumber == 0 && fileTrackNumber == 0 && tagTrackNumber == 0)
            return false;

        return Math.Abs(trackNumber - fileTrackNumber) == 0
            || Math.Abs(trackNumber - trackIndex) == 0
            || (int)Math.Abs(trackNumber - tagTrackNumber) == 0;
    }

    private static bool CompareTrackName(MediaFile mediaFile, MusicBrainzTrack track)
    {
        string trackTitle = track.Title;
        string tagTitle =
            mediaFile.TagFile?.Tag?.Title ?? Path.GetFileNameWithoutExtension(mediaFile.Name);
        string fileTitle =
            mediaFile.Parsed?.Title ?? Path.GetFileNameWithoutExtension(mediaFile.Name);

        if (
            string.IsNullOrEmpty(trackTitle)
            && string.IsNullOrEmpty(fileTitle)
            && string.IsNullOrEmpty(tagTitle)
        )
            return false;

        return fileTitle.ContainsSanitized(trackTitle) || tagTitle.ContainsSanitized(trackTitle);
    }

    public async Task<int> DeleteVideoFilesByHostFolderAsync(string hostFolder)
    {
        return await context
            .VideoFiles.Where(vf => vf.HostFolder == hostFolder)
            .ExecuteDeleteAsync();
    }

    public async Task<int> DeleteMetadataByHostFolderAsync(string hostFolder)
    {
        return await context.Metadata.Where(m => m.HostFolder == hostFolder).ExecuteDeleteAsync();
    }

    public async Task<int> UpdateVideoFilePathsAsync(
        string oldHostFolder,
        string oldFilename,
        string newHostFolder,
        string newFilename
    )
    {
        string newFolder = "/" + StoragePathHelpers.GetName(newHostFolder);

        return await context
            .VideoFiles.Where(vf => vf.HostFolder == oldHostFolder && vf.Filename == oldFilename)
            .ExecuteUpdateAsync(setters =>
                setters
                    .SetProperty(vf => vf.HostFolder, newHostFolder)
                    .SetProperty(vf => vf.Filename, newFilename)
                    .SetProperty(vf => vf.Folder, newFolder)
            );
    }

    public async Task<int> UpdateVideoFileSubtitlesAsync(
        Ulid videoFileId,
        string subtitlesJson,
        CancellationToken ct = default
    )
    {
        return await context
            .VideoFiles.Where(vf => vf.Id == videoFileId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(vf => vf.Subtitles, subtitlesJson),
                ct
            );
    }

    /// <inheritdoc />
    public async Task<int> RepointPreviewTracksAsync(
        string hostFolder,
        string sheetFileName,
        string vttFileName,
        CancellationToken ct = default
    )
    {
        // Stored normalised by the scan, so the driver-shaped path a job carries
        // has to be brought to the same shape before it will match anything.
        string folder = hostFolder.Replace("\\", "/");

        List<VideoFile> videoFiles = await context
            .VideoFiles.Include(videoFile => videoFile.Metadata)
            .Where(videoFile => videoFile.HostFolder == folder)
            .ToListAsync(ct);

        int repointed = 0;

        foreach (VideoFile videoFile in videoFiles)
        {
            // Read once: the property deserialises the column on every get, so
            // editing what a second get returns would edit a different array.
            VideoTrack[] tracks = videoFile.Tracks;
            bool changed = false;

            foreach (VideoTrack track in tracks)
            {
                string? fileName = track.Kind switch
                {
                    "sprite" => sheetFileName,
                    "thumbnails" => vttFileName,
                    _ => null,
                };

                if (fileName is null || track.File == $"/{fileName}")
                    continue;

                track.File = $"/{fileName}";
                changed = true;
            }

            // A legacy folder holds a `sprite` sheet and no cue file, so there is
            // no `thumbnails` track to repoint and the rebuild left the clients
            // with nothing to read — both of them resolve the scrub preview from
            // the `thumbnails` entry alone. Adding it here is what makes the
            // rebuilt VTT reachable before the next full scan.
            if (tracks.All(track => track.Kind != "thumbnails"))
            {
                tracks =
                [
                    .. tracks,
                    new VideoTrack { File = $"/{vttFileName}", Kind = "thumbnails" },
                ];
                changed = true;
            }

            // The watch response reads the preview names from the metadata before
            // the track rows, so it has to name the rebuilt pair as well.
            if (RepointPreviewMetadata(videoFile.Metadata, sheetFileName, vttFileName))
                changed = true;

            if (!changed)
                continue;

            videoFile.Tracks = tracks;
            repointed++;
        }

        if (repointed > 0)
            await context.SaveChangesAsync(ct);

        return repointed;
    }

    private static bool RepointPreviewMetadata(
        Metadata? metadata,
        string sheetFileName,
        string vttFileName
    )
    {
        List<IPreview>? previews = metadata?.Previews;
        if (metadata is null || previews is not { Count: > 0 })
            return false;

        bool changed = false;
        foreach (IPreview preview in previews)
        {
            if (
                preview.ImageFileName == $"/{sheetFileName}"
                && preview.TimeFileName == $"/{vttFileName}"
            )
                continue;

            preview.ImageFileName = $"/{sheetFileName}";
            preview.TimeFileName = $"/{vttFileName}";
            changed = true;
        }

        if (changed)
            metadata._previews = JsonConvert.SerializeObject(previews);

        return changed;
    }

    public async Task<List<RecordedVideoFileLocation>> GetRecordedVideoFileLocationsByMovieIdAsync(
        int movieId
    )
    {
        return await context
            .VideoFiles.AsNoTracking()
            .Where(vf => vf.MovieId == movieId)
            .Select(vf => new RecordedVideoFileLocation(vf.Share, vf.HostFolder, vf.Filename))
            .ToListAsync();
    }

    public async Task<List<RecordedVideoFileLocation>> GetRecordedVideoFileLocationsByTvIdAsync(
        int tvId
    )
    {
        List<int> episodeIds = await context
            .Episodes.AsNoTracking()
            .Where(e => e.TvId == tvId)
            .Select(e => e.Id)
            .ToListAsync();

        return await context
            .VideoFiles.AsNoTracking()
            .Where(vf => vf.EpisodeId != null && episodeIds.Contains(vf.EpisodeId.Value))
            .Select(vf => new RecordedVideoFileLocation(vf.Share, vf.HostFolder, vf.Filename))
            .ToListAsync();
    }

    public async Task DeleteVideoFilesAndMetadataByMovieIdAsync(int movieId)
    {
        List<Ulid> metadataIds = await context
            .VideoFiles.Where(vf => vf.MovieId == movieId && vf.MetadataId != null)
            .Select(vf => vf.MetadataId!.Value)
            .ToListAsync();

        await context.VideoFiles.Where(vf => vf.MovieId == movieId).ExecuteDeleteAsync();

        if (metadataIds.Count > 0)
        {
            await context.Metadata.Where(m => metadataIds.Contains(m.Id)).ExecuteDeleteAsync();
        }
    }

    public async Task DeleteVideoFilesAndMetadataByTvIdAsync(int tvId)
    {
        List<int> episodeIds = await context
            .Episodes.Where(e => e.TvId == tvId)
            .Select(e => e.Id)
            .ToListAsync();

        List<Ulid> metadataIds = await context
            .VideoFiles.Where(vf =>
                vf.EpisodeId != null
                && episodeIds.Contains(vf.EpisodeId.Value)
                && vf.MetadataId != null
            )
            .Select(vf => vf.MetadataId!.Value)
            .ToListAsync();

        await context
            .VideoFiles.Where(vf => vf.EpisodeId != null && episodeIds.Contains(vf.EpisodeId.Value))
            .ExecuteDeleteAsync();

        if (metadataIds.Count > 0)
        {
            await context.Metadata.Where(m => metadataIds.Contains(m.Id)).ExecuteDeleteAsync();
        }
    }

    // Carries just enough of a VideoFiles row to decide, client-side, whether
    // it is stale — SQLite has no APPLY, so a server-side .Contains() against
    // a client-built (Share, HostFolder, Filename) record cannot be
    // translated; the flat projection + in-memory filter below is the
    // documented two-step workaround.
    private readonly record struct StaleVideoFileCandidate(
        Ulid Id,
        string Share,
        string HostFolder,
        string Filename,
        Ulid? MetadataId
    );

    private static List<Ulid> StaleIds(
        List<StaleVideoFileCandidate> candidates,
        HashSet<RecordedVideoFileLocation> storedKeys
    ) =>
        [
            .. candidates
                .Where(c => !storedKeys.Contains(new(c.Share, c.HostFolder, c.Filename)))
                .Select(c => c.Id),
        ];

    private async Task DeleteVideoFilesAndTheirMetadataAsync(
        List<StaleVideoFileCandidate> candidates,
        List<Ulid> idsToDelete
    )
    {
        if (idsToDelete.Count == 0)
            return;

        List<Ulid> metadataIds =
        [
            .. candidates
                .Where(c => idsToDelete.Contains(c.Id) && c.MetadataId != null)
                .Select(c => c.MetadataId!.Value),
        ];

        await context.VideoFiles.Where(vf => idsToDelete.Contains(vf.Id)).ExecuteDeleteAsync();

        if (metadataIds.Count > 0)
            await context.Metadata.Where(m => metadataIds.Contains(m.Id)).ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    /// <remarks>
    /// No wrapping transaction — same rationale as
    /// <see cref="DeleteVideoFilesAndMetadataByMovieIdAsync"/>: this runs
    /// after a full re-store, and holding the SQLite writer lock across it
    /// would block every other workload for no benefit.
    /// </remarks>
    public async Task DeleteStaleVideoFilesAndMetadataByMovieIdAsync(
        int movieId,
        List<string> eligibleShares,
        HashSet<RecordedVideoFileLocation> storedKeys
    )
    {
        if (eligibleShares.Count == 0)
            return;

        List<StaleVideoFileCandidate> candidates = await context
            .VideoFiles.Where(vf => vf.MovieId == movieId && eligibleShares.Contains(vf.Share))
            .Select(vf => new StaleVideoFileCandidate(
                vf.Id,
                vf.Share,
                vf.HostFolder,
                vf.Filename,
                vf.MetadataId
            ))
            .ToListAsync();

        await DeleteVideoFilesAndTheirMetadataAsync(candidates, StaleIds(candidates, storedKeys));
    }

    /// <inheritdoc />
    /// <remarks>
    /// No wrapping transaction — same rationale as
    /// <see cref="DeleteVideoFilesAndMetadataByTvIdAsync"/>.
    /// </remarks>
    public async Task DeleteStaleVideoFilesAndMetadataByTvIdAsync(
        int tvId,
        List<string> eligibleShares,
        HashSet<RecordedVideoFileLocation> storedKeys
    )
    {
        if (eligibleShares.Count == 0)
            return;

        List<int> episodeIds = await context
            .Episodes.Where(e => e.TvId == tvId)
            .Select(e => e.Id)
            .ToListAsync();

        List<StaleVideoFileCandidate> candidates = await context
            .VideoFiles.Where(vf =>
                vf.EpisodeId != null
                && episodeIds.Contains(vf.EpisodeId.Value)
                && eligibleShares.Contains(vf.Share)
            )
            .Select(vf => new StaleVideoFileCandidate(
                vf.Id,
                vf.Share,
                vf.HostFolder,
                vf.Filename,
                vf.MetadataId
            ))
            .ToListAsync();

        await DeleteVideoFilesAndTheirMetadataAsync(candidates, StaleIds(candidates, storedKeys));
    }

    public List<DirectoryTree> GetDirectoryTree(string folder = "")
    {
        List<DirectoryTree> array = [];

        if (string.IsNullOrEmpty(folder) || folder == "/")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                DriveInfo[] driveInfo = DriveInfo.GetDrives();
                return
                [
                    .. driveInfo
                        .Where(d => d.IsReady)
                        .Select(d => new DirectoryTree(d.RootDirectory.ToString(), ""))
                        .OrderBy(file => file.Path),
                ];
            }

            folder = "/";
        }

        if (!storageDriver.DirectoryExists(folder))
            return array;

        IEnumerable<string> directories;
        try
        {
            directories = storageDriver
                .EnumerateFileSystemEntries(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(e => storageDriver.DirectoryExists(e));
        }
        catch (IOException)
        {
            return array;
        }
        catch (UnauthorizedAccessException)
        {
            return array;
        }

        array =
        [
            .. directories.Select(d => new DirectoryTree(folder, d)).OrderBy(file => file.Path),
        ];

        return array;
    }

    public Task<List<VideoFile>> SearchVideoFilesAsync(
        string? query,
        int limit,
        CancellationToken ct = default
    )
    {
        IQueryable<VideoFile> baseQuery = context
            .VideoFiles.AsNoTracking()
            .Include(file => file.Movie)
            .Include(file => file.Episode)
                .ThenInclude(episode => episode!.Tv);

        IQueryable<VideoFile> filtered = string.IsNullOrEmpty(query)
            ? baseQuery
            : baseQuery.Where(file =>
                EF.Functions.Like(file.Filename, $"%{query}%")
                || (file.Movie != null && EF.Functions.Like(file.Movie.Title, $"%{query}%"))
                || (file.Episode != null && EF.Functions.Like(file.Episode.Title!, $"%{query}%"))
                || (
                    file.Episode != null
                    && file.Episode.Tv != null
                    && EF.Functions.Like(file.Episode.Tv.Title, $"%{query}%")
                )
            );

        return filtered.OrderByDescending(file => file.UpdatedAt).Take(limit).ToListAsync(ct);
    }
}
