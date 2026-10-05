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

// ---------------------------------------------------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.NmSystem;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.AcoustId;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
[Serializable]
public class ReleaseImportJob : AbstractMusicFolderJob
{
    public ReleaseImportJob() { }

    public ReleaseImportJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        IAudioFingerprinter audioFingerprinter,
        ILoggerFactory loggerFactory
    )
        : base(storageFactory, storageDriver, audioFingerprinter, loggerFactory) { }

    public override string QueueName => "import";

    // The queue reserves by OrderByDescending(Priority), so the old 4 sat below
    // PersonRefreshJob's 5 and below the show/movie extras at 6, and a manual import
    // never got the single import worker while any of that was outstanding. Matching 6
    // was not enough either: equal priorities fall back to insertion order, so a fresh
    // import still queued behind every enrichment job already waiting.
    //
    // This is what "Add selection" dispatches, with someone watching the dashboard for
    // it, so it outranks background enrichment outright rather than tying with it.
    public override int Priority => 9;

    // private bool _fromFingerprint;

    public override async Task Handle()
    {
        Log.LogInformation(
            "ReleaseImportJob: {InputFolder} -> library {LibraryId} folder {FolderId} release {ReleaseId}",
            InputFolder,
            LibraryId,
            FolderId,
            ReleaseId
        );

        await using MediaContext context = new();
        JobDispatcher jobDispatcher = new();

        Library albumLibrary = await context
            .Libraries.Where(f => f.Id == LibraryId)
            .Include(f => f.FolderLibraries)
                .ThenInclude(f => f.Folder)
            .FirstAsync();

        await using MediaScan mediaScan = new(StorageDriver);
        ConcurrentBag<MediaFolderExtend> rootFolders = await mediaScan
            .DisableRegexFilter()
            .Process(InputFolder, 1);

        if (rootFolders.Count == 0)
        {
            Log.LogTrace("Processing folder: {InputFolder}", InputFolder);
            Folder? baseFolder = ResolveDestinationFolder(albumLibrary, InputFolder);
            if (baseFolder is null)
            {
                Log.LogWarning(
                    "ReleaseImportJob: no library folder contains {InputFolder}; skipping",
                    InputFolder
                );
                return;
            }

            jobDispatcher.DispatchJob<AudioImportJob>(LibraryId, baseFolder.Id, InputFolder);
            return;
        }

        Parallel.ForEach(
            rootFolders,
            SystemParallelism.Options,
            folder =>
            {
                Log.LogInformation("Processing folder: {Path}", folder.Path);
                Folder? baseFolder = ResolveDestinationFolder(albumLibrary, folder.Path);
                if (baseFolder is null)
                {
                    Log.LogWarning(
                        "ReleaseImportJob: no library folder contains {Path}; skipping",
                        folder.Path
                    );
                    return;
                }

                jobDispatcher.DispatchJob<AudioImportJob>(LibraryId, baseFolder.Id, folder.Path);
            }
        );
    }

    /// <summary>
    /// The configured library folder whose root contains <paramref name="absolutePath"/>,
    /// or null when none does. The root is resolved facade-first
    /// (<see cref="LibraryManager.ResolveScanRoot"/>): a local library's root lives in
    /// the storage facade, and the raw driver's GetFullPath canonicalizes the
    /// scope-relative folder path against the process working directory instead —
    /// so a driver-only resolve produced a bogus "/app/Libraries/Music" root, matched
    /// nothing, and the old <c>.First(...)</c> threw "Sequence contains no matching
    /// element" for every music release on a local/UNC library.
    /// </summary>
    /// <summary>
    /// Where this release is being imported TO.
    /// <para>
    /// "Add new content" picks a destination folder and hands its id over in
    /// <see cref="AbstractMusicFolderJob.FolderId"/>, while the path it hands over is the
    /// SOURCE — a download or staging folder that is deliberately outside the library.
    /// Deriving the destination by asking which library folder contains the source
    /// therefore never matched, and every manual music import was skipped with
    /// "no library folder contains …". The operator already answered this question;
    /// the answer is honoured here.
    /// </para>
    /// <para>
    /// The library scan dispatches this job with only a path (the folder is already in
    /// the library and no destination was chosen), so that route still resolves by
    /// containment.
    /// </para>
    /// </summary>
    private Folder? ResolveDestinationFolder(Library library, string absolutePath)
    {
        if (FolderId != default)
        {
            Folder? chosen = library
                .FolderLibraries.Select(folderLibrary => folderLibrary.Folder)
                .FirstOrDefault(folder => folder.Id == FolderId);

            if (chosen is not null)
                return chosen;

            Log.LogWarning(
                "ReleaseImportJob: destination folder {FolderId} is not in library {LibraryId}; falling back to the folder containing {Path}",
                FolderId,
                LibraryId,
                absolutePath
            );
        }

        return MatchLibraryFolder(library, absolutePath);
    }

    private Folder? MatchLibraryFolder(Library library, string absolutePath) =>
        library
            .FolderLibraries.Select(folderLibrary => folderLibrary.Folder)
            .FirstOrDefault(f =>
                absolutePath.StartsWith(
                    LibraryManager.ResolveScanRoot(
                        StorageFactory.For(f.Id, f.DriverId, string.Empty),
                        f.Path
                    ),
                    StringComparison.OrdinalIgnoreCase
                )
            );
}
