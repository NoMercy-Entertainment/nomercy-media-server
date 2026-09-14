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
using NoMercy.Data.Jobs;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage;
using NoMercyQueue;

namespace NoMercy.Data.Services;

public class LibraryLogic(
    Ulid id,
    IDbContextFactory<MediaContext> mediaContextFactory,
    IStorageDriver storageDriver,
    IStorageFactory storageFactory,
    ILogger<LibraryLogic> logger
)
{
    private readonly IStorageDriver _storageDriver = storageDriver;
    private Library Library { get; set; } = new();

    public Ulid Id { get; set; } = id;

    private int Depth { get; set; }

    public List<dynamic> Titles { get; } = [];
    private List<Folder> FolderList { get; } = [];

    public async Task<bool> Process()
    {
        await using MediaContext mediaContext = await mediaContextFactory.CreateDbContextAsync();

        Library? library = await mediaContext
            .Libraries.AsNoTracking()
            .Include(library => library.FolderLibraries)
                .ThenInclude(folderLibrary => folderLibrary.Folder)
            .FirstOrDefaultAsync(library => library.Id == Id);

        if (library is null)
            return false;

        Library = library;

        FolderList.AddRange(Library.FolderLibraries.Select(folderLibrary => folderLibrary.Folder));

        GetDepth();

        await ScanFolder();

        return true;
    }

    private void GetDepth()
    {
        Depth = Library.Type switch
        {
            "music" => 3,
            _ => 1,
        };
    }

    private async Task ScanFolder()
    {
        foreach (Folder folder in FolderList)
            switch (Library?.Type)
            {
                case "music":
                    await ScanAudioFolder(folder);
                    break;
            }

        logger.LogInformation("Scanning done");
    }

    private async Task ScanAudioFolder(Folder folder)
    {
        IStorage folderStorage = storageFactory.For(folder.Id, folder.DriverId, string.Empty);
        // Resolve through the driver, not the IStorage facade: the facade's
        // GetFullPath is a LocalStorage-only escape hatch that throws on every
        // remote backend, so a facade call here killed every rescan of an
        // NFS / SMB / S3 / WebDAV library. The driver resolves the path within
        // its own backend, exactly as MediaScan.Process does internally.
        string scanRoot = folderStorage.Driver.GetFullPath(folder.Path);

        await using MediaScan mediaScan = new(folderStorage.Driver);
        IEnumerable<MediaFolderExtend> rootFolders =
        [
            .. (await mediaScan.DisableRegexFilter().Process(scanRoot, 2)).SelectMany(r =>
                r.SubFolders ?? []
            ),
        ];

        foreach (MediaFolderExtend rootFolder in rootFolders)
        {
            if (rootFolder.Path == scanRoot)
                return;

            Titles.Add(rootFolder.Path);

            logger.LogTrace("Processing {Path}", rootFolder.Path);

            MusicJob musicJob = new(rootFolder.Path, Library);
            QueueRunner.Current!.Dispatcher.Dispatch(musicJob);
        }

        logger.LogInformation("Found {Count} subfolders", Titles.Count);
    }
}
