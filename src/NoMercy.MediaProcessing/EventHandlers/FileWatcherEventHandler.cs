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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Providers.TMDB.Models.TV;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.EventHandlers;

public class FileWatcherEventHandler : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly SemaphoreSlim _semaphore = new(2);
    private readonly IStorageDriver _storageDriver;
    private readonly IStorageFactory _storageFactory;

    private readonly ILogger<FileWatcherEventHandler> _logger;

    public FileWatcherEventHandler(
        ILogger<FileWatcherEventHandler> logger,
        IEventBus eventBus,
        IStorageDriver storageDriver,
        IStorageFactory storageFactory
    )
    {
        _logger = logger;
        _storageDriver = storageDriver;
        _storageFactory = storageFactory;
        _subscriptions.Add(eventBus.Subscribe<FileCreatedEvent>(OnFileCreated));
        _subscriptions.Add(eventBus.Subscribe<FileDeletedEvent>(OnFileDeleted));
        _subscriptions.Add(eventBus.Subscribe<FileRenamedEvent>(OnFileRenamed));
    }

    internal async Task OnFileCreated(FileCreatedEvent @event, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogInformation(
                "FileWatcher: Processing new/changed content in {FolderPath}",
                @event.FolderPath
            );

            MediaScan mediaScan = new(_storageDriver);
            MediaScan scan = mediaScan.EnableFileListing();

            if (@event.LibraryType == MediaTypes.MusicMediaType)
                scan.DisableRegexFilter();

            ConcurrentBag<MediaFolderExtend> mediaFolders = await scan.Process(@event.FolderPath);

            if (mediaFolders.Count == 0)
            {
                _logger.LogWarning(
                    "FileWatcher: No media found in {FolderPath}",
                    @event.FolderPath
                );
                return;
            }

            MediaFolderExtend mediaFolder = mediaFolders.First();

            switch (@event.LibraryType)
            {
                case MediaTypes.InboxMediaType:
                    return;
                case MediaTypes.MovieMediaType:
                    await HandleMovieFolder(@event, mediaFolder);
                    break;
                case MediaTypes.TvMediaType:
                case MediaTypes.AnimeMediaType:
                    await HandleTvFolder(@event, mediaFolder);
                    break;
                case MediaTypes.MusicMediaType:
                    HandleMusicFolder(@event, mediaFolder);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "FileWatcher: Error processing {FolderPath}: {Message}",
                [@event.FolderPath, ex.Message]
            );
        }
        finally
        {
            _semaphore.Release();
        }
    }

    internal async Task OnFileDeleted(FileDeletedEvent @event, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation(
                "FileWatcher: Processing deletion of {FullPath}",
                @event.FullPath
            );

            string hostFolder = Path.GetDirectoryName(@event.FullPath).OrEmpty();
            string filename = "/" + Path.GetFileName(@event.FullPath);

            await using MediaContext mediaContext = new();
            FileRepository fileRepository = new(mediaContext, _storageDriver);

            List<int> affectedMovieIds = await mediaContext
                .VideoFiles.Where(vf =>
                    vf.HostFolder == hostFolder && vf.Filename == filename && vf.MovieId != null
                )
                .Select(vf => vf.MovieId!.Value)
                .Distinct()
                .ToListAsync(ct);
            List<int> affectedTvIds = await mediaContext
                .VideoFiles.Where(vf =>
                    vf.HostFolder == hostFolder && vf.Filename == filename && vf.EpisodeId != null
                )
                .Join(
                    mediaContext.Episodes,
                    vf => vf.EpisodeId,
                    episode => episode.Id,
                    (vf, episode) => episode.TvId
                )
                .Distinct()
                .ToListAsync(ct);

            int videoFilesDeleted = await fileRepository.DeleteVideoFileByPathAsync(
                hostFolder,
                filename
            );
            int metadataDeleted = await fileRepository.DeleteMetadataByPathAsync(
                hostFolder,
                filename
            );

            _logger.LogInformation(
                "FileWatcher: Deleted {VideoFilesDeleted} video file(s) and {MetadataDeleted} metadata record(s) for {HostFolder}",
                [videoFilesDeleted, metadataDeleted, hostFolder]
            );

            if (videoFilesDeleted > 0 && EventBusProvider.IsConfigured)
            {
                await EventBusProvider.Current.PublishAsync(
                    new LibraryRefreshedEvent { QueryKey = ["base", "libraries"] }
                );

                // A library-list invalidation doesn't reach an already-open movie/show
                // page — its query key is scoped to that item's id, so it needs its own
                // invalidation or the client shows stale (deleted) episodes until a
                // manual refresh, same as FileManager.FindFiles does for full rescans.
                foreach (int movieId in affectedMovieIds)
                {
                    await EventBusProvider.Current.PublishAsync(
                        new LibraryRefreshedEvent { QueryKey = ["movie", movieId.ToString()] }
                    );
                }
                foreach (int tvId in affectedTvIds)
                {
                    await EventBusProvider.Current.PublishAsync(
                        new LibraryRefreshedEvent { QueryKey = ["tv", tvId.ToString()] }
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "FileWatcher: Error processing deletion of {FullPath}: {Message}",
                [@event.FullPath, ex.Message]
            );
        }
    }

    internal async Task OnFileRenamed(FileRenamedEvent @event, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation(
                "FileWatcher: Processing rename from {OldFullPath} to {NewFullPath}",
                [@event.OldFullPath, @event.NewFullPath]
            );

            string oldHostFolder = Path.GetDirectoryName(@event.OldFullPath).OrEmpty();
            string oldFilename = "/" + Path.GetFileName(@event.OldFullPath);
            string newHostFolder = Path.GetDirectoryName(@event.NewFullPath).OrEmpty();
            string newFilename = "/" + Path.GetFileName(@event.NewFullPath);

            await using MediaContext mediaContext = new();
            FileRepository fileRepository = new(mediaContext, _storageDriver);

            int updated = await fileRepository.UpdateVideoFilePathsAsync(
                oldHostFolder,
                oldFilename,
                newHostFolder,
                newFilename
            );

            if (updated > 0)
            {
                _logger.LogInformation(
                    "FileWatcher: Updated {Updated} video file path(s) from {OldHostFolder} to {NewHostFolder}",
                    [updated, oldHostFolder, newHostFolder]
                );

                if (EventBusProvider.IsConfigured)
                {
                    await EventBusProvider.Current.PublishAsync(
                        new LibraryRefreshedEvent { QueryKey = ["base", "libraries"] }
                    );
                }
            }
            else
            {
                _logger.LogDebug(
                    "FileWatcher: No matching records found for rename, treating as new content"
                );
                await OnFileCreated(
                    new()
                    {
                        FolderPath = newHostFolder,
                        LibraryId = @event.LibraryId,
                        LibraryType = @event.LibraryType,
                    },
                    ct
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "FileWatcher: Error processing rename from {OldFullPath} to {NewFullPath}: {Message}",
                [@event.OldFullPath, @event.NewFullPath, ex.Message]
            );
        }
    }

    private async Task HandleMovieFolder(FileCreatedEvent @event, MediaFolderExtend mediaFolder)
    {
        if (mediaFolder.Parsed.Title is null)
        {
            _logger.LogWarning("FileWatcher: Could not parse title from {Path}", mediaFolder.Path);
            return;
        }

        _logger.LogInformation(
            "FileWatcher: Movie {Path}: Searching TMDB for '{Title}'",
            [mediaFolder.Path, mediaFolder.Parsed.Title]
        );

        using TmdbSearchClient tmdbSearchClient = new();
        TmdbPaginatedResponse<TmdbMovie>? response = await tmdbSearchClient.Movie(
            mediaFolder.Parsed.Title,
            mediaFolder.Parsed.Year
        );

        if (response?.Results is null || response.Results.Count == 0)
        {
            _logger.LogWarning(
                "FileWatcher: No TMDB results found for movie '{Title}'",
                mediaFolder.Parsed.Title
            );
            return;
        }

        TmdbMovie? movie = response.Results.MaxBy(result =>
            ScoreCandidate(
                result.Title,
                result.ReleaseDate,
                mediaFolder.Parsed.Title,
                mediaFolder.Parsed.Year
            )
        );
        if (
            movie is null
            || FuzzyMatcher.MatchPercentage(movie.Title, mediaFolder.Parsed.Title)
                < MinMatchConfidence
        )
        {
            _logger.LogWarning(
                "FileWatcher: No confident TMDB match for movie '{Title}'",
                mediaFolder.Parsed.Title
            );
            return;
        }

        _logger.LogInformation(
            "FileWatcher: Movie '{Title}' found on TMDB (ID: {Id}), dispatching job",
            [movie.Title, movie.Id]
        );

        JobDispatcher jobDispatcher = new();
        jobDispatcher.DispatchJob<MovieImportJob>(movie.Id, @event.LibraryId);
    }

    private async Task HandleTvFolder(FileCreatedEvent @event, MediaFolderExtend mediaFolder)
    {
        if (mediaFolder.Parsed.Title is null)
        {
            _logger.LogWarning("FileWatcher: Could not parse title from {Path}", mediaFolder.Path);
            return;
        }

        _logger.LogInformation(
            "FileWatcher: TV Show {Path}: Searching TMDB for '{Title}'",
            [mediaFolder.Path, mediaFolder.Parsed.Title]
        );

        using TmdbSearchClient tmdbSearchClient = new();
        TmdbPaginatedResponse<TmdbTvShow>? response = await tmdbSearchClient.TvShow(
            mediaFolder.Parsed.Title,
            mediaFolder.Parsed.Year
        );

        if (response?.Results is null || response.Results.Count == 0)
        {
            _logger.LogWarning(
                "FileWatcher: No TMDB results found for TV show '{Title}'",
                mediaFolder.Parsed.Title
            );
            return;
        }

        TmdbTvShow? show = response.Results.MaxBy(result =>
            ScoreCandidate(
                result.Name,
                result.FirstAirDate,
                mediaFolder.Parsed.Title,
                mediaFolder.Parsed.Year
            )
        );
        if (
            show is null
            || FuzzyMatcher.MatchPercentage(show.Name, mediaFolder.Parsed.Title)
                < MinMatchConfidence
        )
        {
            _logger.LogWarning(
                "FileWatcher: No confident TMDB match for TV show '{Title}'",
                mediaFolder.Parsed.Title
            );
            return;
        }

        _logger.LogInformation(
            "FileWatcher: TV Show '{Name}' found on TMDB (ID: {Id}), dispatching job",
            [show.Name, show.Id]
        );

        JobDispatcher jobDispatcher = new();
        jobDispatcher.DispatchJob<ShowImportJob>(show.Id, @event.LibraryId);
    }

    private void HandleMusicFolder(FileCreatedEvent @event, MediaFolderExtend mediaFolder)
    {
        _logger.LogInformation("FileWatcher: Music {Path}: Processing", mediaFolder.Path);

        string directoryPath = Path.GetFullPath(mediaFolder.Path);

        using MediaContext mediaContext = new();
        Library? library = mediaContext
            .Libraries.Include(l => l.FolderLibraries)
                .ThenInclude(fl => fl.Folder)
            .FirstOrDefault(l => l.Id == @event.LibraryId);

        if (library is null)
            return;

        FolderLibrary? folderLibrary = library.FolderLibraries.FirstOrDefault(f =>
        {
            // Resolve through the driver, not the IStorage facade: the facade's
            // GetFullPath is a LocalStorage-only escape hatch that throws on
            // every remote backend, so a facade call here killed folder
            // matching for NFS / SMB music libraries.
            string driverRoot = _storageFactory
                .For(f.Folder.Id, f.Folder.DriverId, string.Empty)
                .Driver.GetFullPath(f.Folder.Path);
            return directoryPath.StartsWith(driverRoot, StringComparison.OrdinalIgnoreCase);
        });
        if (folderLibrary is null)
            return;

        JobDispatcher jobDispatcher = new();
        jobDispatcher.DispatchJob<AudioImportJob>(
            @event.LibraryId,
            folderLibrary.FolderId,
            directoryPath
        );
    }

    private const double MinMatchConfidence = 50.0;

    private static double ScoreCandidate(
        string candidateTitle,
        DateTime? candidateDate,
        string parsedTitle,
        string? parsedYear
    )
    {
        double score = FuzzyMatcher.MatchPercentage(candidateTitle, parsedTitle);
        if (int.TryParse(parsedYear, out int year) && candidateDate?.Year == year)
            score += 25;
        return score;
    }

    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
        _semaphore.Dispose();
    }
}
