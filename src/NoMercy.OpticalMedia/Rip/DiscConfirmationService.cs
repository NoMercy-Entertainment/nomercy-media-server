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

using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage;

namespace NoMercy.OpticalMedia.Rip;

public class DiscConfirmationService(
    ILibraryRepository libraryRepository,
    IStorageFactory storageFactory,
    IStorageDriver storageDriver,
    IEventBus eventBus
) : IDiscConfirmationService
{
    public async Task<DiscConfirmationResult> ConfirmAsync(
        Ulid folderId,
        Ulid libraryId,
        string ripOutputPath,
        CustomMetadata metadata,
        CancellationToken ct
    )
    {
        Folder? targetFolder = await libraryRepository.GetLibraryFolder(folderId);
        if (targetFolder is null)
            return new(DiscConfirmationOutcome.FolderNotFound);

        Library? targetLibrary = await libraryRepository.GetLibraryByIdWithFolders(libraryId);
        if (targetLibrary is null)
            return new(DiscConfirmationOutcome.LibraryNotFound);

        // DrivePath and title index are irrelevant to the output path here — the file is
        // already ripped, this only renames/moves it — so a title-less synthetic request is
        // enough for RipOutputPathHelper to build the canonical library-relative path.
        RipRequest syntheticRequest = new(
            DrivePath: string.Empty,
            SelectedTitleIndices: [0],
            MetadataId: null,
            Custom: metadata,
            LibraryId: libraryId,
            FolderId: folderId,
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: [],
            Mode: RipMode.RipAndEncode
        );

        string folderRelative = RipOutputPathHelper.Build(
            syntheticRequest,
            targetLibrary.Type,
            titleIndex: 0,
            batchIndex: 0
        );

        IStorage folderStorage = storageFactory.For(
            targetFolder.Id,
            targetFolder.DriverId,
            string.Empty
        );

        string parentRelative = ParentRelative(folderRelative);
        if (!string.IsNullOrEmpty(parentRelative))
            await folderStorage.CreateDirectoryAsync(parentRelative, ct);

        await using (Stream src = storageDriver.OpenRead(ripOutputPath))
        await using (
            Stream dst = await folderStorage.OpenWriteAsync(folderRelative, overwrite: true, ct)
        )
        {
            await src.CopyToAsync(dst, ct);
        }

        try
        {
            storageDriver.DeleteFile(ripOutputPath);
        }
        catch
        {
            // best effort
        }

        string watcherFolderHost = ResolveHostPath(folderStorage, parentRelative);
        await eventBus.PublishAsync(
            new FileCreatedEvent
            {
                FolderPath = watcherFolderHost,
                LibraryId = targetLibrary.Id,
                LibraryType = targetLibrary.Type,
            }
        );

        return new(DiscConfirmationOutcome.Confirmed, folderRelative);
    }

    private static string ParentRelative(string folderRelative)
    {
        int slash = folderRelative.LastIndexOf('/');
        return slash <= 0 ? "" : folderRelative[..slash];
    }

    private static string ResolveHostPath(IStorage storage, string subPath)
    {
        try
        {
            return storage.GetFullPath(subPath);
        }
        catch
        {
            return subPath;
        }
    }
}
