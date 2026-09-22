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
using MovieFileLibrary;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Analysis;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.PluginSdk.Hooks;
using NoMercy.Storage;
using Serilog.Events;
using Logger = NoMercy.NmSystem.SystemCalls.Logger;

namespace NoMercy.MediaProcessing.Files;

public partial class FileManager(
    IFileRepository fileRepository,
    IStorageFactory storageFactory,
    IStorageDriver storageDriver,
    IMediaAnalyzer mediaAnalyzer,
    IFilenameParserPipeline filenameParser,
    IPluginMediaSourceProvider? pluginMediaSources = null
) : IFileManager
{
    private readonly FilenameResolver _resolver = new(filenameParser);

    private IStorage StorageFor(Folder folder) =>
        storageFactory.For(folder.Id, folder.DriverId, string.Empty);

    /// <summary>
    /// Re-reads what every scanned name MEANS through the same resolver the file
    /// list uses.
    /// <para>
    /// The scan derives season and episode with its own detector plus a local
    /// regex, so a rescan saw none of the naming the file list had learned to
    /// read: a version suffix ("- 01v2") hid the episode entirely, a
    /// season-scoped special ("S01OVA05") landed on a real episode of season one,
    /// a half episode ("S01E21.5") took the whole episode's place, and a title
    /// was cut in half at its own year ("Fairy Tail ("). Two parsers answering
    /// one question is the defect; the scan owns the IO, this owns the meaning.
    /// </para>
    /// <para>
    /// Music keeps the scan's own result — its disc and track numbers come from
    /// the file's tags, which a name parser knows nothing about.
    /// </para>
    /// </summary>
    private void ReResolveNames(string libraryType)
    {
        if (libraryType == MediaTypes.MusicMediaType)
            return;

        foreach (MediaFolderExtend folder in Files)
        foreach (MediaFile file in folder.Files ?? [])
        {
            if (file.Parsed is null)
                continue;

            MovieFile resolved = _resolver
                .Resolve(
                    Path.GetFileName(file.Path),
                    Path.GetDirectoryName(file.Path),
                    file.Path,
                    libraryType
                )
                .Parsed;

            file.Parsed = new()
            {
                Title = resolved.Title,
                Year = resolved.Year,
                Season = resolved.Season,
                Episode = resolved.Episode,
                IsSeries = resolved.IsSeries,
                IsSuccess = resolved.IsSuccess,
                FilePath = file.Parsed.FilePath,
                DiscNumber = file.Parsed.DiscNumber,
                TrackNumber = file.Parsed.TrackNumber,
            };
        }
    }

    private int Id { get; set; }
    private Movie? Movie { get; set; }
    private Tv? Show { get; set; }

    private List<Folder> Folders { get; set; } = [];

    /// <summary>
    /// Set by <c>Paths</c> when at least one of the library's root folders reads back.
    /// False also covers "never asked", which is the safe direction: nothing is cleared.
    /// </summary>
    private bool AnyLibraryRootReadable { get; set; }

    /// <summary>
    /// The library's root folder rows as <c>Paths</c> read them, kept so the delete
    /// guard can re-open the same storages without a second query.
    /// </summary>
    private IReadOnlyList<Folder> LibraryRootFolders { get; set; } = [];
    private List<MediaFolderExtend> Files { get; set; } = [];
    public string Type { get; set; } = "";

    /// <summary>
    /// The Folder each scanned item was actually enumerated from, keyed by
    /// reference. StoreVideoItem used to re-derive this by testing whether
    /// the item's path contained a Folder's stored path — an unanchored
    /// substring check that a short, root-scoped folder path (e.g. an
    /// empty-root folder's bare title name) could win against a longer,
    /// correct folder from a completely different driver. The scan already
    /// knows the answer at discovery time; carrying it here is what lets
    /// StoreVideoItem trust it instead of re-guessing.
    /// </summary>
    private readonly Dictionary<MediaFile, Folder> _itemOriginFolder = [];

    /// <summary>
    /// The (Share, HostFolder, Filename) of every VideoFile this pass
    /// actually re-stored — see <see cref="ReconcileStaleVideoFilesAsync"/>.
    /// </summary>
    private readonly HashSet<RecordedVideoFileLocation> _storedVideoFileKeys = [];

    /// <summary>
    /// The Share (Folders.Id) of every folder that had at least one item
    /// <see cref="StoreVideoItem"/> could not resolve this pass. A Share in
    /// here never enters the eligible-shares set — see
    /// <see cref="ReconcileStaleVideoFilesAsync"/>.
    /// </summary>
    private readonly HashSet<string> _sharesWithSkippedItems = new(
        StringComparer.OrdinalIgnoreCase
    );

    private string? Filter { get; set; }

    /// <summary>
    /// The episode id this encode job was dispatched for, set alongside
    /// <see cref="FilterFiles"/> by the post-encode scan. <see cref="StoreVideoItem"/>
    /// uses this id directly for the file the scan filtered down to, instead of
    /// re-deriving the episode from its filename via
    /// <see cref="IFileRepository.GetEpisode"/> — the filename parser can land on
    /// the wrong episode when a title contains digits that themselves read as a
    /// season/episode (e.g. South Park's "1%" parsing as S00E12 instead of the
    /// dispatched S15E12). The correct id is already known at dispatch time; this
    /// hint carries it through instead of throwing it away and re-guessing it.
    /// Left null on every other call path (initial import, manual rescan), which
    /// keeps their behavior — filename-derived matching — unchanged.
    /// </summary>
    private int? DispatchedMediaId { get; set; }

    /// <summary>
    /// Tags every item <paramref name="scannedFolders"/> carries with the
    /// Folder it was actually enumerated from — see
    /// <see cref="_itemOriginFolder"/>.
    /// </summary>
    private void RecordItemOrigins(IEnumerable<MediaFolderExtend> scannedFolders, Folder origin)
    {
        foreach (MediaFolderExtend scannedFolder in scannedFolders)
        foreach (MediaFile file in scannedFolder.Files ?? [])
            _itemOriginFolder[file] = origin;
    }

    /// <summary>
    /// Deletes only the stale VideoFiles rows a completed pass is safe to
    /// reconcile — never a folder this pass could not fully verify. A Share
    /// (Folders.Id) is "eligible" when it was actually scanned this pass
    /// (present in <see cref="Folders"/>) AND none of its items were skipped
    /// (<see cref="_sharesWithSkippedItems"/>); within an eligible Share, only
    /// rows whose key is not in <see cref="_storedVideoFileKeys"/> — genuinely
    /// no longer on disk — are removed. Runs AFTER <c>StoreTvShow</c> /
    /// <c>StoreMovie</c> so both sets reflect what this pass actually did,
    /// which is what makes the guard possible: the previous unconditional
    /// delete ran BEFORE storage and could not tell "nothing here" from
    /// "everything here failed to resolve" (issue #55).
    /// </summary>
    private async Task ReconcileStaleVideoFilesAsync(Library library)
    {
        List<string> eligibleShares =
        [
            .. Folders
                .Select(folder => folder.Id.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(share => !_sharesWithSkippedItems.Contains(share)),
        ];

        if (eligibleShares.Count == 0)
        {
            Logger.App(
                $"[FindFiles] {Type} id={Id}: every scanned folder had a skipped item — "
                    + "preserving all existing records rather than reconciling a partial result",
                LogEventLevel.Warning
            );
            return;
        }

        switch (library.Type)
        {
            case MediaTypes.MovieMediaType:
                await fileRepository.DeleteStaleVideoFilesAndMetadataByMovieIdAsync(
                    Id,
                    eligibleShares,
                    _storedVideoFileKeys
                );
                break;
            case MediaTypes.TvMediaType:
            case MediaTypes.AnimeMediaType:
                await fileRepository.DeleteStaleVideoFilesAndMetadataByTvIdAsync(
                    Show?.Id ?? Id,
                    eligibleShares,
                    _storedVideoFileKeys
                );
                break;
        }
    }

    public async Task<bool> FindFiles(int id, Library library)
    {
        Id = id;
        _itemOriginFolder.Clear();
        _storedVideoFileKeys.Clear();
        _sharesWithSkippedItems.Clear();

        await MediaType(id, library);

        Folders = Paths(library, Movie, Show);

        foreach (Folder folder in Folders)
        {
            // Pass the whole folder so GetFiles can resolve the right driver
            // (local / NFS / S3) for it. Hardcoding _storageDriver was
            // scanning every library against the local disk regardless of
            // its actual backend — NFS NAS and S3 buckets returned 0 files.
            ConcurrentBag<MediaFolderExtend> files = await GetFiles(library, folder);

            if (!files.IsEmpty)
            {
                Files.AddRange(files);
                RecordItemOrigins(files, folder);
            }

            // What plugins found under the same folder, added before the names
            // are resolved so their files go through the same parser as the
            // scanner's own. Anything that skipped it has no episode, no tags
            // and no duration, and the store drops it again without a word.
            IReadOnlyList<MediaFolderExtend> fromPlugins = await (
                pluginMediaSources ?? NullPluginMediaSourceProvider.Instance
            ).ScanAsync(folder.Path);

            if (fromPlugins.Count > 0)
            {
                Files.AddRange(fromPlugins);
                RecordItemOrigins(fromPlugins, folder);
            }
        }

        ReResolveNames(library.Type);

        // How many playable files the scan actually resolved. Logged next to the
        // per-type candidate count so an empty result is distinguishable from a
        // scan that found files but failed to parse them.
        int rawFileCount = Files.Sum(folder => folder.Files?.Count ?? 0);
        bool hasCandidates = Files
            .SelectMany(folder => folder.Files ?? [])
            .Any(file => file.Parsed is not null);

        Logger.App(
            $"[FindFiles] {Type} id={id}: scan resolved {rawFileCount} file(s), "
                + $"{(hasCandidates ? "has" : "no")} parseable candidates across {Folders.Count} folder(s)",
            LogEventLevel.Information
        );

        // A completed pass reconciles its OWN stale rows after storage runs
        // (ReconcileStaleVideoFilesAsync, below) — that is what lets it tell
        // "nothing here" from "everything here failed to resolve". This
        // branch only ever handles the OTHER failure shape: the scan found no
        // parseable candidates AT ALL. A rescan that comes back completely
        // empty — a transient remote-storage hiccup, or a scan-side
        // regression — must NOT wipe a show/movie that is still fully on
        // disk, so that case is confirmed against the recorded rows below
        // before anything is removed. Genuine on-disk deletions are also
        // reconciled by the file-watcher's FileDeletedEvent path.
        if (Filter is null && !hasCandidates && AnyLibraryRootReadable)
        {
            // A readable library root says nothing about THIS title: a root registered one
            // level above where the media actually lives reads back fine and resolves every
            // title to nothing, and deleting on that wipes a library that is fully on disk.
            // The registered rows carry the path each file was last seen at, so ask storage
            // directly and only delete once the media itself is unreachable.
            if (await RecordedMediaStillReadable(library))
            {
                Logger.App(
                    $"[FindFiles] {Type} id={id}: nothing resolved but the registered media is "
                        + "still readable — preserving records (resolution failed, the media did not)",
                    LogEventLevel.Warning
                );
            }
            else
            {
                Logger.App(
                    $"[FindFiles] {Type} id={id}: library root readable, nothing resolved and the "
                        + "registered media is gone — removing the video file and metadata records",
                    LogEventLevel.Information
                );

                switch (library.Type)
                {
                    case MediaTypes.MovieMediaType:
                        await fileRepository.DeleteVideoFilesAndMetadataByMovieIdAsync(id);
                        break;
                    case MediaTypes.TvMediaType:
                    case MediaTypes.AnimeMediaType:
                        await fileRepository.DeleteVideoFilesAndMetadataByTvIdAsync(Show?.Id ?? id);
                        break;
                }
            }
        }
        else if (Filter is null && !hasCandidates)
        {
            Logger.App(
                $"[FindFiles] {Type} id={id}: scan found no parseable files and the library root "
                    + "did not read back — preserving existing records (an outage is not a deletion)",
                LogEventLevel.Warning
            );
        }

        switch (library.Type)
        {
            case MediaTypes.MovieMediaType:
                await StoreMovie();
                break;
            case MediaTypes.TvMediaType:
            case MediaTypes.AnimeMediaType:
                await StoreTvShow();
                break;
            case MediaTypes.MusicMediaType:
                await StoreMusic();
                break;
            default:
                Logger.App("Unknown library type");
                break;
        }

        if (Filter is null && hasCandidates)
            await ReconcileStaleVideoFilesAsync(library);

        // Publish refresh events only after successful commit
        switch (library.Type)
        {
            case MediaTypes.MovieMediaType:
                if (EventBusProvider.IsConfigured)
                {
                    await EventBusProvider.Current.PublishAsync(
                        new LibraryRefreshedEvent
                        {
                            QueryKey = ["libraries", library.Id.ToString()],
                        }
                    );
                    // Info-page invalidation: this is the choke point every
                    // Movie scan path (encoder finalize, manual rescan, initial
                    // import) runs through, so publishing here covers them all
                    // instead of duplicating the publish at each call site.
                    await EventBusProvider.Current.PublishAsync(
                        new LibraryRefreshedEvent { QueryKey = ["movie", id.ToString()] }
                    );
                }
                break;
            case MediaTypes.TvMediaType:
            case MediaTypes.AnimeMediaType:
                if (EventBusProvider.IsConfigured)
                {
                    await EventBusProvider.Current.PublishAsync(
                        new LibraryRefreshedEvent
                        {
                            QueryKey = ["libraries", library.Id.ToString()],
                        }
                    );
                    // Anime shows have no /anime/:id route on the client — they
                    // render at /tv/:id, so an anime-type library's info-page
                    // key is "tv", never "anime".
                    await EventBusProvider.Current.PublishAsync(
                        new LibraryRefreshedEvent { QueryKey = ["tv", (Show?.Id ?? id).ToString()] }
                    );
                }
                break;
            case MediaTypes.MusicMediaType:
                if (EventBusProvider.IsConfigured)
                    await EventBusProvider.Current.PublishAsync(
                        new LibraryRefreshedEvent { QueryKey = ["music"] }
                    );
                break;
        }

        return hasCandidates;
    }

    public void FilterFiles(string filter)
    {
        Filter = filter;
    }

    /// <summary>
    /// Records the episode id this scan was dispatched for. See
    /// <see cref="DispatchedMediaId"/> for why <see cref="StoreVideoItem"/> trusts
    /// this over the filename parser for the filtered file.
    /// </summary>
    public void HintDispatchedMediaId(int mediaId)
    {
        DispatchedMediaId = mediaId;
    }

    public async Task MoveToLibraryFolder(int id, Folder folder)
    {
        await using MediaContext context = new();

        Tv? tv = await context
            .Tvs.Include(tv => tv.Library)
                .ThenInclude(lib => lib.FolderLibraries)
                    .ThenInclude(folderLibrary => folderLibrary.Folder)
            .Include(tv => tv.Episodes)
                .ThenInclude(e => e.VideoFiles)
            .FirstOrDefaultAsync(t => t.Id == id);

        Movie? movie = await context
            .Movies.Include(movie => movie.Library)
                .ThenInclude(lib => lib.FolderLibraries)
                    .ThenInclude(folderLibrary => folderLibrary.Folder)
            .Include(movie => movie.VideoFiles)
            .FirstOrDefaultAsync(movie => movie.Id == id);

        string folderName = "";
        string sourceFolder = "";
        IStorage? sourceStorage = null;

        if (tv?.Folder is not null)
            foreach (FolderLibrary libraryFolder in tv.Library.FolderLibraries)
            {
                IStorage folderStorage = StorageFor(libraryFolder.Folder);
                string folderRoot = ResolveBackendPath(folderStorage, libraryFolder.Folder.Path);
                string path = folderStorage.CombinePath(folderRoot, tv.Folder);
                if (!folderStorage.Exists(path))
                {
                    string? match = FileNameSanitizer.FindMatchingDirectory(
                        storageDriver,
                        folderRoot,
                        tv.Folder.Replace("/", "")
                    );
                    if (match != null)
                        path = match;
                }

                if (!folderStorage.Exists(path))
                    continue;

                folderName = tv.Folder;
                sourceFolder = path;
                sourceStorage = folderStorage;

                break;
            }
        else if (movie?.Folder is not null)
            foreach (FolderLibrary libraryFolder in movie.Library.FolderLibraries)
            {
                IStorage folderStorage = StorageFor(libraryFolder.Folder);
                string folderRoot = ResolveBackendPath(folderStorage, libraryFolder.Folder.Path);
                string path = folderStorage.CombinePath(folderRoot, movie.Folder);
                if (!folderStorage.Exists(path))
                {
                    string? match = FileNameSanitizer.FindMatchingDirectory(
                        storageDriver,
                        folderRoot,
                        movie.Folder.Replace("/", "")
                    );
                    if (match != null)
                        path = match;
                }

                if (!folderStorage.Exists(path))
                    continue;

                folderName = movie.Folder;
                sourceFolder = path;
                sourceStorage = folderStorage;

                break;
            }

        if (
            string.IsNullOrEmpty(folderName)
            || string.IsNullOrEmpty(sourceFolder)
            || sourceStorage is null
        )
        {
            Logger.App("Folder not found");
            return;
        }

        IStorage destinationStorage = StorageFor(folder);
        string destinationRoot = ResolveBackendPath(destinationStorage, folder.Path);
        string destinationFolder = destinationStorage.CombinePath(destinationRoot, folderName);

        Logger.App($"Moving {sourceFolder} to {destinationFolder}");

        await MoveFolderAsync(sourceFolder, destinationFolder, sourceStorage, destinationStorage);

        FolderLibrary? newFolderLibrary = await context
            .FolderLibrary.Include(fl => fl.Library)
            .Include(fl => fl.Folder)
            .FirstOrDefaultAsync(fl => fl.FolderId == folder.Id);

        if (newFolderLibrary is null)
            return;

        if (tv?.Folder is not null)
        {
            tv.Folder = folderName;
            tv.LibraryId = newFolderLibrary.LibraryId;

            // LibraryTv.LibraryId is part of its composite primary key
            // (LibraryId, TvId) — EF Core rejects mutating a PK column on a
            // tracked entity ("part of a key and so cannot be modified").
            // Repointing to the new library is a delete of the old link row
            // plus an insert of the new one, never an in-place update.
            LibraryTv? libraryTv = await context.LibraryTv.FirstOrDefaultAsync(lt =>
                lt.TvId == tv.Id
            );

            if (libraryTv is not null)
            {
                context.LibraryTv.Remove(libraryTv);
                context.LibraryTv.Add(new(newFolderLibrary.LibraryId, tv.Id));
            }

            await context.SaveChangesAsync();
        }
        else if (movie?.Folder is not null)
        {
            movie.Folder = folderName;
            movie.LibraryId = newFolderLibrary.LibraryId;

            // See the LibraryTv comment above — LibraryMovie.LibraryId is
            // equally part of its composite primary key.
            LibraryMovie? libraryMovie = await context.LibraryMovie.FirstOrDefaultAsync(lm =>
                lm.MovieId == movie.Id
            );

            if (libraryMovie is not null)
            {
                context.LibraryMovie.Remove(libraryMovie);
                context.LibraryMovie.Add(new(newFolderLibrary.LibraryId, movie.Id));
            }

            await context.SaveChangesAsync();
        }

        _ = await FindFiles(id, newFolderLibrary.Library);
    }
}
