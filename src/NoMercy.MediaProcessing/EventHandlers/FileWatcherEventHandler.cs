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
using NoMercy.MediaProcessing.Libraries;
using NoMercy.MediaProcessing.Music;
using NoMercy.MediaProcessing.Recordings;
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
    private readonly IDbContextFactory<MediaContext>? _contextFactory;

    private readonly ILogger<FileWatcherEventHandler> _logger;

    public FileWatcherEventHandler(
        ILogger<FileWatcherEventHandler> logger,
        IEventBus eventBus,
        IStorageDriver storageDriver,
        IStorageFactory storageFactory,
        IDbContextFactory<MediaContext>? contextFactory = null
    )
    {
        _logger = logger;
        _storageDriver = storageDriver;
        _storageFactory = storageFactory;
        _contextFactory = contextFactory;
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

    internal static string HostFolderOf(string fullPath) =>
        Path.GetDirectoryName(fullPath).OrEmpty().Replace('\\', '/');

    /// <summary>
    /// The library folder a directory on disk lives under. The longest match
    /// wins, so a folder nested inside another library folder is its own root.
    /// </summary>
    internal static Folder? LibraryFolderOf(string directory, IEnumerable<Folder> folders) =>
        folders
            .Where(folder =>
                StoragePathHelpers.TryGetLibraryRelativeFolder(directory, folder.Path, out _)
            )
            .MaxBy(folder => folder.Path.Length);

    /// <summary>
    /// Every form in which a row can hold this directory as its HostFolder.
    /// <para>
    /// The scanner stores it rebased onto the library folder
    /// (<see cref="StoragePathHelpers.RebaseToFolderRoot"/>), which drops the
    /// leading slash of a Linux path. Rows stored before that still hold the
    /// directory as it is on disk. The watcher reports the path on disk, so
    /// comparing it as-is matched only the old rows (#367).
    /// </para>
    /// </summary>
    internal static List<string> StoredHostFoldersOf(string directory, Folder? libraryFolder)
    {
        List<string> forms = [];
        if (libraryFolder is not null)
            forms.Add(StoragePathHelpers.RebaseToFolderRoot(directory, libraryFolder.Path));
        forms.Add(directory);

        return [.. forms.Distinct()];
    }

    private static Task<List<Folder>> LibraryFoldersAsync(
        MediaContext mediaContext,
        Ulid libraryId,
        CancellationToken ct
    ) =>
        mediaContext
            .FolderLibrary.AsNoTracking()
            .Where(link => link.LibraryId == libraryId)
            .Select(link => link.Folder)
            .ToListAsync(ct);

    internal async Task OnFileDeleted(FileDeletedEvent @event, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation(
                "FileWatcher: Processing deletion of {FullPath}",
                @event.FullPath
            );

            string directory = HostFolderOf(@event.FullPath);
            string filename = "/" + Path.GetFileName(@event.FullPath);

            await using MediaContext mediaContext = await OpenContextAsync(ct);
            FileRepository fileRepository = new(mediaContext, _storageDriver);

            List<Folder> libraryFolders = await LibraryFoldersAsync(
                mediaContext,
                @event.LibraryId,
                ct
            );
            List<string> hostFolders = StoredHostFoldersOf(
                directory,
                LibraryFolderOf(directory, libraryFolders)
            );

            if (@event.LibraryType == MediaTypes.MusicMediaType)
            {
                await MarkMusicFileMissingAsync(mediaContext, hostFolders, filename, ct);
                return;
            }

            List<int> affectedMovieIds = await mediaContext
                .VideoFiles.Where(vf =>
                    hostFolders.Contains(vf.HostFolder)
                    && vf.Filename == filename
                    && vf.MovieId != null
                )
                .Select(vf => vf.MovieId!.Value)
                .Distinct()
                .ToListAsync(ct);
            List<int> affectedTvIds = await mediaContext
                .VideoFiles.Where(vf =>
                    hostFolders.Contains(vf.HostFolder)
                    && vf.Filename == filename
                    && vf.EpisodeId != null
                )
                .Join(
                    mediaContext.Episodes,
                    vf => vf.EpisodeId,
                    episode => episode.Id,
                    (vf, episode) => episode.TvId
                )
                .Distinct()
                .ToListAsync(ct);

            int videoFilesDeleted = 0;
            int metadataDeleted = 0;
            foreach (string hostFolder in hostFolders)
            {
                videoFilesDeleted += await fileRepository.DeleteVideoFileByPathAsync(
                    hostFolder,
                    filename
                );
                metadataDeleted += await fileRepository.DeleteMetadataByPathAsync(
                    hostFolder,
                    filename
                );
            }

            _logger.LogInformation(
                "FileWatcher: Deleted {VideoFilesDeleted} video file(s) and {MetadataDeleted} metadata record(s) for {HostFolder}",
                [videoFilesDeleted, metadataDeleted, directory]
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

            string oldDirectory = HostFolderOf(@event.OldFullPath);
            string oldFilename = "/" + Path.GetFileName(@event.OldFullPath);
            string newDirectory = HostFolderOf(@event.NewFullPath);
            string newFilename = "/" + Path.GetFileName(@event.NewFullPath);

            await using MediaContext mediaContext = await OpenContextAsync(ct);
            FileRepository fileRepository = new(mediaContext, _storageDriver);

            List<Folder> libraryFolders = await LibraryFoldersAsync(
                mediaContext,
                @event.LibraryId,
                ct
            );
            Folder? newLibraryFolder = LibraryFolderOf(newDirectory, libraryFolders);

            if (
                @event.LibraryType == MediaTypes.MusicMediaType
                && await HandleMusicRenameAsync(mediaContext, @event, libraryFolders, ct)
            )
                return;

            // Stored the way the scanner stores it: HostFolder rebased onto the
            // library folder, Folder measured from that folder's root. A file
            // moved out of every library folder has no such form, and is left
            // to the new-content path below.
            int updated = 0;
            if (
                newLibraryFolder is not null
                && StoragePathHelpers.TryGetLibraryRelativeFolder(
                    newDirectory,
                    newLibraryFolder.Path,
                    out string newFolder
                )
            )
            {
                string newHostFolder = StoragePathHelpers.RebaseToFolderRoot(
                    newDirectory,
                    newLibraryFolder.Path
                );
                List<string> oldHostFolders = StoredHostFoldersOf(
                    oldDirectory,
                    LibraryFolderOf(oldDirectory, libraryFolders)
                );

                foreach (string oldHostFolder in oldHostFolders)
                    updated += await fileRepository.UpdateVideoFilePathsAsync(
                        oldHostFolder,
                        oldFilename,
                        newHostFolder,
                        newFilename,
                        newFolder
                    );
            }

            if (updated > 0)
            {
                _logger.LogInformation(
                    "FileWatcher: Updated {Updated} video file path(s) from {OldHostFolder} to {NewHostFolder}",
                    [updated, oldDirectory, newDirectory]
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
                        FolderPath = newDirectory,
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

    private async Task MarkMusicFileMissingAsync(
        MediaContext mediaContext,
        List<string> hostFolders,
        string filename,
        CancellationToken ct
    )
    {
        List<Guid> trackIds = await mediaContext
            .Tracks.AsNoTracking()
            .Where(track =>
                track.HostFolder != null
                && hostFolders.Contains(track.HostFolder)
                && track.Filename == filename
            )
            .Select(track => track.Id)
            .ToListAsync(ct);

        int marked = await new MissingTrackPruner(mediaContext, _logger).MarkMissingAsync(
            trackIds,
            DateTime.UtcNow,
            ct
        );
        _logger.LogInformation(
            "FileWatcher: Marked {Marked} track row(s) unavailable for {Filename}",
            [marked, filename]
        );
    }

    // True when the rename was settled here; false sends it down the import path.
    private async Task<bool> HandleMusicRenameAsync(
        MediaContext mediaContext,
        FileRenamedEvent @event,
        List<Folder> libraryFolders,
        CancellationToken ct
    )
    {
        string newPath = @event.NewFullPath.Replace('\\', '/');
        string newDirectory = HostFolderOf(newPath);
        string oldDirectory = HostFolderOf(@event.OldFullPath);
        Folder? libraryFolder = LibraryFolderOf(newDirectory, libraryFolders);
        if (libraryFolder is null)
            return false;

        if (_storageDriver.DirectoryExists(newPath))
        {
            IStorage storage = _storageFactory.For(libraryFolder.Id, libraryFolder.DriverId, "");
            MusicReconcileResult result = await new MusicTrackReconciler(
                mediaContext,
                _logger
            ).ReconcileAsync(
                libraryFolder.Id,
                LibraryManager.ResolveScanRoot(storage, libraryFolder.Path),
                storage.Driver,
                [newPath, @event.OldFullPath.Replace('\\', '/')],
                cancellationToken: ct
            );
            return result.Moved > 0;
        }

        if (
            !StoragePathHelpers.TryGetLibraryRelativeParts(
                newPath,
                libraryFolder.Path,
                out string folder,
                out string filename
            )
        )
            return false;

        string oldFilename = "/" + Path.GetFileName(@event.OldFullPath);
        RecordingRepository repository = new(mediaContext);
        int repointed = 0;
        foreach (
            string oldHostFolder in StoredHostFoldersOf(
                oldDirectory,
                LibraryFolderOf(oldDirectory, libraryFolders)
            )
        )
            repointed += await repository.RepointTrackFile(
                oldHostFolder,
                oldFilename,
                new(libraryFolder.Id, folder, newDirectory, filename)
            );

        if (repointed == 0)
            return false;

        await new MissingTrackPruner(mediaContext, _logger).ClearMissingAsync(
            await mediaContext
                .Tracks.AsNoTracking()
                .Where(track => track.HostFolder == newDirectory && track.Filename == filename)
                .Select(track => track.Id)
                .ToListAsync(ct),
            ct
        );
        _logger.LogInformation(
            "FileWatcher: Repointed {Count} track row(s) from {Old} to {New}",
            [repointed, @event.OldFullPath, newPath]
        );
        return true;
    }

    private async Task<MediaContext> OpenContextAsync(CancellationToken ct) =>
        _contextFactory is null ? new() : await _contextFactory.CreateDbContextAsync(ct);

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
