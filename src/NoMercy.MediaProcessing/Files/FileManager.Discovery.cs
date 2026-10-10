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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;
using Serilog.Events;
using Logger = NoMercy.NmSystem.SystemCalls.Logger;

namespace NoMercy.MediaProcessing.Files;

public partial class FileManager
{
    private async Task MediaType(int id, Library library)
    {
        (Movie, Show, Type) = await fileRepository.MediaType(id, library);
    }

    private async Task<ConcurrentBag<MediaFolderExtend>> GetFiles(Library library, Folder folder)
    {
        IStorage folderStorage = storageFactory.For(folder.Id, folder.DriverId, string.Empty);
        string scanRoot = ResolveBackendPath(folderStorage, folder.Path);
        MediaScan mediaScan = new(folderStorage.Driver);

        int depth = library.Type switch
        {
            MediaTypes.MovieMediaType => 1,
            MediaTypes.TvMediaType or MediaTypes.AnimeMediaType => 2,
            _ => 0,
        };

        ConcurrentBag<MediaFolderExtend> folders = await mediaScan
            .EnableFileListing()
            .DisableRegexFilter()
            .FilterByMediaType(library.Type)
            .FilterByFileName(Filter)
            .Process(scanRoot, depth);

        await mediaScan.DisposeAsync();

        return folders;
    }

    private static string ResolveBackendPath(IStorage storage, string scopeRelativePath) =>
        storage.ResolveBackendPath(scopeRelativePath);

    private List<Folder> Paths(Library library, Movie? movie = null, Tv? show = null)
    {
        List<Folder> folders = [];
        string? folder = library.Type switch
        {
            MediaTypes.MovieMediaType => movie?.Folder?.Replace("/", ""),
            MediaTypes.TvMediaType or MediaTypes.AnimeMediaType => show?.Folder?.Replace("/", ""),
            _ => "",
        };

        if (folder == null)
        {
            Logger.App("Folder not set");
            return folders;
        }

        using MediaContext mediaContext = new();

        // Scope to the target library's folders only. The previous query
        // grabbed every FolderLibrary row system-wide, so a rescan of a movie
        // in an NFS library would also probe every S3 / WebDAV folder for
        // unrelated libraries — one flaky remote backend then threw on
        // Exists() and killed the whole job (retried up to maxAttempts).
        Folder[] rootFolders =
        [
            .. mediaContext
                .FolderLibrary.Where(fl => fl.LibraryId == library.Id)
                .Include(fl => fl.Folder)
                    .ThenInclude(fl => fl.Driver)
                .Select(f => f.Folder),
        ];

        LibraryRootFolders = rootFolders;

        foreach (Folder rootFolder in rootFolders)
        {
            IStorage folderStorage = StorageFor(rootFolder);

            // Whether the library's own root reads back, recorded from the same rows and
            // the same facade the resolution below uses. An empty result cannot say on
            // its own whether a title's media was deleted or the storage holding every
            // title went away, and those two need opposite handling downstream.
            if (TryExists(folderStorage, rootFolder.Path))
                AnyLibraryRootReadable = true;

            // Stay in scope-relative space: rootFolder.Path is the storage key
            // as the facade wants it (e.g. "Marvels/TV.Shows"), and the facade
            // Exists / List / downstream scan all reject absolute paths. Do NOT
            // resolve to an OS/mount-absolute path here — GetFullPath (facade
            // OR driver) produces "/mnt/vault/..." which StoragePathGuard then
            // rejects as a scope-relative key.
            string path = folderStorage.CombinePath(rootFolder.Path, folder);

            // Treat a transport-level failure from any single backend as
            // "not in this folder" rather than aborting the whole rescan. The
            // job is idempotent on its successful folders, and one transient
            // S3 502 should not trigger queue-level retries.
            bool exists = TryExists(folderStorage, path);

            if (!exists)
            {
                // FindMatchingDirectory walks the raw driver (DirectoryExists /
                // EnumerateFileSystemEntries), so it needs the driver-absolute
                // root and returns a driver-absolute directory. Convert that hit
                // back to a scope-relative key before storing / re-checking, so
                // the facade and the downstream scan keep working in one space.
                string resolvedRoot = ResolveBackendPath(folderStorage, rootFolder.Path);
                string? match = TryFindMatchingDirectory(
                    folderStorage.Driver,
                    resolvedRoot,
                    folder
                );
                if (match != null)
                {
                    // match is the driver-absolute hit EnumerateFileSystemEntries
                    // returned — OS-separated (backslash on Windows). GetName splits
                    // on '/' only (Rule 2), so an un-normalized match reads as having
                    // no separator at all and comes back unchanged, doubling the path
                    // when combined with rootFolder.Path below.
                    path = folderStorage.CombinePath(
                        rootFolder.Path,
                        folderStorage.GetName(match.Replace('\\', '/'))
                    );
                    exists = TryExists(folderStorage, path);
                }
            }

            if (exists)
                folders.Add(
                    new()
                    {
                        Path = path,
                        Id = rootFolder.Id,
                        DriverId = rootFolder.DriverId,
                        Driver = rootFolder.Driver,
                    }
                );
        }

        return folders;
    }

    /// <summary>
    /// Whether any path this title is already registered under still reads back.
    /// A rescan resolving nothing is a claim about the SCAN; this is the check that
    /// turns it into a claim about the MEDIA, and only that justifies a delete.
    /// </summary>
    private async Task<bool> RecordedMediaStillReadable(Library library)
    {
        List<RecordedVideoFileLocation> recorded = library.Type switch
        {
            MediaTypes.MovieMediaType =>
                await fileRepository.GetRecordedVideoFileLocationsByMovieIdAsync(Id),
            MediaTypes.TvMediaType or MediaTypes.AnimeMediaType =>
                await fileRepository.GetRecordedVideoFileLocationsByTvIdAsync(Show?.Id ?? Id),
            _ => [],
        };

        foreach (RecordedVideoFileLocation location in recorded)
        {
            Folder? folder = ResolveRecordedFolder(location.Share);
            if (folder is null)
                continue;

            IStorage storage = StorageFor(folder);
            string path = storage.CombinePath(location.HostFolder, location.Filename);

            if (TryExists(storage, path))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The library root a registered file belongs to. <c>Share</c> holds a
    /// <c>Folders.Id</c>; rows written before that was reliable fall back to the
    /// library's first root, whose driver and scope root are the ones that matter.
    /// </summary>
    private Folder? ResolveRecordedFolder(string share) =>
        LibraryRootFolders.FirstOrDefault(rootFolder => rootFolder.Id.ToString() == share)
        ?? LibraryRootFolders.FirstOrDefault();

    private static bool TryExists(IStorage storage, string path)
    {
        try
        {
            return storage.Exists(path);
        }
        catch (Exception ex)
        {
            Logger.App(
                $"[FileManager.Paths] storage.Exists threw for '{path}' on driver {storage.Driver.GetType().Name}: {ex.Message}",
                LogEventLevel.Warning
            );
            return false;
        }
    }

    private static string? TryFindMatchingDirectory(
        IStorageDriver driver,
        string rootPath,
        string expectedFolderName
    )
    {
        try
        {
            return FileNameSanitizer.FindMatchingDirectory(driver, rootPath, expectedFolderName);
        }
        catch (Exception ex)
        {
            Logger.App(
                $"[FileManager.Paths] FindMatchingDirectory threw for root '{rootPath}' on driver {driver.GetType().Name}: {ex.Message}",
                LogEventLevel.Warning
            );
            return null;
        }
    }

    // A scan verifies completeness, not integrity, so it must not stream every
    // file's bytes over the network to hash them — that turned each rescan into a
    // full re-read of every playlist, subtitle, font, and (multi-megabyte) preview
    // sprite. Fingerprint the file by size + last-write time instead: two cheap
    // stats, no content read. The value still changes whenever the file changes,
    // which is all any consumer needs (only fonts.FileHash is read downstream, for
    // cache-busting), while a several-second sprite read collapses to a stat.
    private static string ComputeFileHash(IStorage storage, string filePath)
    {
        long size = storage.SizeOrZero(filePath);

        long modifiedTicks;
        try
        {
            modifiedTicks = storage.LastModified(filePath).UtcTicks;
        }
        catch
        {
            modifiedTicks = 0;
        }

        using SHA256 sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes($"{size}:{modifiedTicks}"));

        StringBuilder hashStringBuilder = new(64);

        foreach (byte b in hashBytes)
            hashStringBuilder.Append(b.ToString("x2"));
        return hashStringBuilder.ToString();
    }

    // Liberal subtitle filename matcher: 2-3 char language (ISO 639-1 or -3), arbitrary
    // type (alt, full, sign, song, sdh, forced, commentary, director, ...), 3-6 char
    // extension (vtt, ass, srt, ssa, sub, idx, sup, vob, webvtt).  Previously the regex
    // only matched 3-char lang + 3-4 char type + 3-char ext and the consumer methods
    // further filtered to type in (sign, song, full) — both combined silently dropped
    // every "alt" / "sdh" / "forced" subtitle file.
    [GeneratedRegex(@"(?<lang>[a-zA-Z]{2,3})\.(?<type>\w+)\.(?<ext>\w{3,6})$")]
    private static partial Regex SubtitleFileRegex();

    [GeneratedRegex(@"#xywh=\d+,\d+,(?<width>\d+),(?<height>\d+)")]
    private static partial Regex ImageDimensions();

    [GeneratedRegex(@"^video_(?<width>\d+)x(?<height>\d+)(?:_(?:SDR|HDR))?$")]
    private static partial Regex VideoDirectoryRegex();

    // Older encodes name the rendition dir `audio_<lang>` (no codec suffix);
    // newer ones `audio_<lang>_<codec>` (e.g. audio_jpn_aac). Match both — the
    // codec group is optional — or a rescan drops the audio group from the
    // rebuilt master and the title plays silent.
    [GeneratedRegex(@"^audio_(?<lang>[a-z]{2,3})(?:_(?<codec>\w+))?$")]
    private static partial Regex AudioDirectoryRegex();
}
