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
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.MediaProcessing.Files;

public partial class LibraryFileWatcher
{
    // ReSharper disable once InconsistentNaming
    private static readonly Lazy<LibraryFileWatcher> _instance = new(() =>
        new(_driverStore!, _storageFactoryStore!)
    );
    public static LibraryFileWatcher Instance => _instance.Value;

    private static IStorageDriver? _driverStore;
    private static IStorageFactory? _storageFactoryStore;

    private static FolderWatcher Fs => field ??= new(_driverStore!);
    private static IStorageDriver StorageDriver => _driverStore!;
    private static IStorageFactory StorageFactory => _storageFactoryStore!;

    private static readonly Dictionary<string, FileChangeGroup> FileChangeGroups = new();
    private static readonly Lock LockObject = new();

    [GeneratedRegex(
        @"^(video_.*|audio_.*|subtitles|fonts|thumbs|metadata|scans|cds.*|NCED|NCOP)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex EncodingOutputRegex();

    private const int Delay = 10;

    private static List<Library> _libraries = [];

    public LibraryFileWatcher(IStorageDriver storageDriver, IStorageFactory storageFactory)
    {
        _driverStore = storageDriver;
        _storageFactoryStore = storageFactory;
        Logger.System("Starting FileSystem Watcher", LogEventLevel.Debug);

        Fs.OnChanged += _onFileChanged;
        Fs.OnCreated += _onFileCreated;
        Fs.OnDeleted += _onFileDeleted;
        Fs.OnRenamed += _onFileRenamed;
        Fs.OnError += _onError;

        RefreshLibraryCache();
        Parallel.ForEach(_libraries, library => AddLibraryWatcher(library));
    }

    public static void RefreshLibraryCache()
    {
        using MediaContext mediaContext = new();
        _libraries =
        [
            .. mediaContext
                .Libraries.Include(library => library.FolderLibraries)
                    .ThenInclude(folderLibrary => folderLibrary.Folder),
        ];
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static Action AddLibraryWatcher(Library library)
    {
        // Resolve through the driver, not the IStorage facade: the facade's
        // GetFullPath is a LocalStorage-only escape hatch that throws on every
        // remote backend. FolderWatcher.CreateWatcher supports network (UNC)
        // paths via IsNetworkPath, so this must resolve for NFS / SMB backends
        // too, not just LocalStorage.
        List<string> paths =
        [
            .. library.FolderLibraries.Select(folderLibrary =>
                StorageFactory
                    .For(folderLibrary.Folder.Id, folderLibrary.Folder.DriverId, string.Empty)
                    .Driver.GetFullPath(folderLibrary.Folder.Path)
            ),
        ];

        List<Action> disposers = [];

        Task.Run(() =>
            {
                disposers = Fs.Watch(paths);
            })
            .Wait();

        return () =>
        {
            foreach (Action disposer in disposers)
                disposer();
        };
    }

    private void _onFileChanged(FileWatcherEventArgs e) => HandleFileChange(e);

    private void _onFileCreated(FileWatcherEventArgs e) => HandleFileChange(e);

    private void _onFileDeleted(FileWatcherEventArgs e) => HandleFileChange(e);

    private void _onFileRenamed(FileWatcherEventArgs e) => HandleFileChange(e);

    private void _onError(FileWatcherEventArgs e)
    {
        Logger.System(e, LogEventLevel.Error);
    }

    private static Library? GetLibraryByPath(string path)
    {
        return _libraries.FirstOrDefault(library =>
            library.FolderLibraries.Any(folderLibrary =>
            {
                // Resolve through the driver, not the IStorage facade: the
                // facade's GetFullPath is a LocalStorage-only escape hatch that
                // throws on every remote backend, so a facade call here killed
                // folder matching for NFS / SMB libraries.
                string driverRoot = StorageFactory
                    .For(folderLibrary.Folder.Id, folderLibrary.Folder.DriverId, string.Empty)
                    .Driver.GetFullPath(folderLibrary.Folder.Path);
                return path.StartsWith(driverRoot, StringComparison.OrdinalIgnoreCase);
            })
        );
    }

    private static bool IsInEncodingOutputDirectory(string fullPath)
    {
        string? directory = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(directory))
        {
            string dirName = Path.GetFileName(directory);
            if (EncodingOutputRegex().IsMatch(dirName))
                return true;

            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }

    private void HandleFileChange(FileWatcherEventArgs e)
    {
        if (IsInEncodingOutputDirectory(e.FullPath))
            return;

        string watcherPath = e.Path;
        Library? library = GetLibraryByPath(watcherPath);

        if (library is null)
            return;

        if (!IsAllowedExtensionForLibrary(library, e.FullPath))
            return;

        if (e.ChangeType != WatcherChangeTypes.Deleted && !Path.Exists(e.FullPath))
            return;

        string folderPath = Path.GetDirectoryName(e.FullPath).OrEmpty();

        if (string.IsNullOrEmpty(folderPath))
            return;

        lock (LockObject)
        {
            if (!FileChangeGroups.TryGetValue(folderPath, out FileChangeGroup? fileChangeGroup))
            {
                fileChangeGroup = new(library, folderPath);
                FileChangeGroups[folderPath] = fileChangeGroup;
            }

            fileChangeGroup.Record(
                e.ChangeType,
                e.FullPath,
                e.ChangeType == WatcherChangeTypes.Renamed ? e.OldFullPath : null
            );

            fileChangeGroup.Timer?.Dispose();
            fileChangeGroup.Timer = new(
                ProcessFileChanges,
                fileChangeGroup,
                TimeSpan.FromSeconds(Delay),
                Timeout.InfiniteTimeSpan
            );
        }
    }

    private static bool IsAllowedExtensionForLibrary(Library library, string path)
    {
        if (StorageDriver.DirectoryExists(path))
            return true;

        switch (library.Type)
        {
            case MediaTypes.MovieMediaType:
            case MediaTypes.TvMediaType:
            case MediaTypes.AnimeMediaType:
                string[] videoExtensions = [".mp4", ".mkv", ".avi", ".webm", ".mov", ".m3u8"];
                return videoExtensions.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase
                );
            case MediaTypes.MusicMediaType:
                string[] audioExtensions = [".mp3", ".flac", ".opus", ".wav", ".m4a"];
                return audioExtensions.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase
                );
            case MediaTypes.InboxMediaType:
                string[] inboxExtensions =
                [
                    ".mp4",
                    ".mkv",
                    ".avi",
                    ".webm",
                    ".mov",
                    ".m3u8",
                    ".mp3",
                    ".flac",
                    ".opus",
                    ".wav",
                    ".m4a",
                ];
                return inboxExtensions.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase
                );
            default:
                return false;
        }
    }

    private void ProcessFileChanges(object? state)
    {
        if (state is not FileChangeGroup group)
            return;

        FileChangeGroup snapshot;
        lock (LockObject)
        {
            snapshot = group.Snapshot();
            FileChangeGroups.Remove(group.FolderPath);
            // The one-shot debounce timer has fired; dispose it so its handle is
            // released now instead of leaking until GC on a busy library.
            group.Timer?.Dispose();
        }

        Task.Run(async () =>
        {
            try
            {
                await PublishFileEvent(snapshot);
            }
            catch (Exception ex)
            {
                Logger.System(
                    $"FileWatcher error processing {snapshot.FolderPath}: {ex.Message}",
                    LogEventLevel.Error
                );
            }
        });
    }

    private static async Task PublishFileEvent(FileChangeGroup group)
    {
        if (!EventBusProvider.IsConfigured)
            return;

        // Deletes and renames go out one by one, in order. Created and changed files
        // share one folder import, so the last of them stands for the rest.
        FileChange? import = group.Changes.LastOrDefault(change =>
            change.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Changed
        );
        foreach (
            FileChange change in group.Changes.Where(change =>
                change.ChangeType is not (WatcherChangeTypes.Created or WatcherChangeTypes.Changed)
            )
        )
            await PublishChange(group, change);

        if (import is not null)
            await PublishChange(group, import);
    }

    private static async Task PublishChange(FileChangeGroup group, FileChange change)
    {
        switch (change.ChangeType)
        {
            case WatcherChangeTypes.Created:
            case WatcherChangeTypes.Changed:
                Logger.System(
                    $"FileWatcher: Publishing FileCreatedEvent for {group.FolderPath}",
                    LogEventLevel.Debug
                );
                await EventBusProvider.Current.PublishAsync(
                    new FileCreatedEvent
                    {
                        FolderPath = group.FolderPath,
                        LibraryId = group.Library.Id,
                        LibraryType = group.Library.Type,
                    }
                );
                break;

            case WatcherChangeTypes.Deleted:
                Logger.System(
                    $"FileWatcher: Publishing FileDeletedEvent for {change.FullPath}",
                    LogEventLevel.Debug
                );
                await EventBusProvider.Current.PublishAsync(
                    new FileDeletedEvent
                    {
                        FullPath = change.FullPath,
                        LibraryId = group.Library.Id,
                        LibraryType = group.Library.Type,
                    }
                );
                break;

            case WatcherChangeTypes.Renamed when change.OldFullPath is not null:
                Logger.System(
                    $"FileWatcher: Publishing FileRenamedEvent from {change.OldFullPath} to {change.FullPath}",
                    LogEventLevel.Debug
                );
                await EventBusProvider.Current.PublishAsync(
                    new FileRenamedEvent
                    {
                        OldFullPath = change.OldFullPath,
                        NewFullPath = change.FullPath,
                        LibraryId = group.Library.Id,
                        LibraryType = group.Library.Type,
                    }
                );
                break;

            case WatcherChangeTypes.Renamed:
                Logger.System(
                    $"FileWatcher: Rename detected but no OldFullPath, treating as Created for {group.FolderPath}",
                    LogEventLevel.Debug
                );
                await EventBusProvider.Current.PublishAsync(
                    new FileCreatedEvent
                    {
                        FolderPath = group.FolderPath,
                        LibraryId = group.Library.Id,
                        LibraryType = group.Library.Type,
                    }
                );
                break;
        }
    }
}
