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
using Microsoft.EntityFrameworkCore.Storage;
using NoMercy.Data.DTOs;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;

namespace NoMercy.Data.Repositories;

public class FolderRepository(MediaContext context) : IFolderRepository
{
    public async Task<Folder?> GetFolderByIdAsync(Ulid folderId)
    {
        return await context
            .Folders.Where(folder => folder.Id == folderId)
            .Include(folder => folder.Driver)
            .Include(folder => folder.FolderLibraries)
                .ThenInclude(folderLibrary => folderLibrary.Library)
            .FirstOrDefaultAsync();
    }

    public Task<Folder?> GetFolderByPathAsync(string requestPath)
    {
        return context.Folders.FirstOrDefaultAsync(folder => folder.Path == requestPath);
    }

    /// <summary>
    /// Composite (DriverId, Path) lookup — matches the real unique index. Use
    /// this instead of <see cref="GetFolderByPathAsync"/> whenever the caller
    /// already knows which driver they want; same sub-path on two different
    /// drivers is legitimate (e.g. NFS+S3 mirrors).
    /// </summary>
    public Task<Folder?> GetFolderByDriverAndPathAsync(Ulid driverId, string requestPath)
    {
        return context.Folders.FirstOrDefaultAsync(f =>
            f.DriverId == driverId && f.Path == requestPath
        );
    }

    public Task<List<Folder>> GetFoldersByLibraryIdAsync(FolderLibraryDto[] folderLibraries)
    {
        return context
            .Folders.Where(folder => folderLibraries.Select(f => f.FolderId).Contains(folder.Id))
            .ToListAsync();
    }

    public Task<List<Folder>> GetFoldersByLibraryIdAsync(Ulid libraryId)
    {
        return context
            .FolderLibrary.Where(fl => fl.LibraryId == libraryId)
            .Select(fl => fl.Folder)
            .ToListAsync();
    }

    public Task<Folder?> GetFolderById(Ulid folderId)
    {
        return context.Folders.Where(folder => folder.Id == folderId).FirstOrDefaultAsync();
    }

    public Task<Folder?> GetFolderByPath(string path)
    {
        return context.Folders.FirstOrDefaultAsync(folder => folder.Path == path);
    }

    public Task<int> AddFolderAsync(Folder folder)
    {
        // Match the real unique index (DriverId, Path). Matching on Path
        // alone made FlexLabs emit ON CONFLICT (Path), which doesn't hit the
        // composite unique → the insert proceeded and tripped the actual
        // UNIQUE on (DriverId, Path), surfacing as a 500 to operators trying
        // to attach an already-known folder to a different library.
        return context
            .Folders.Upsert(folder)
            .On(f => new { f.DriverId, f.Path })
            .WhenMatched((existing, incoming) => new() { Path = incoming.Path })
            .RunAsync();
    }

    public Task<int> AddFolderLibraryAsync(FolderLibrary folderLibrary)
    {
        return context
            .FolderLibrary.Upsert(folderLibrary)
            .On(fl => new { fl.LibraryId, fl.FolderId })
            .WhenMatched((fls, fli) => new() { LibraryId = fli.LibraryId, FolderId = fli.FolderId })
            .RunAsync();
    }

    public Task<int> AddFolderLibraryAsync(FolderLibrary[] folderLibraries)
    {
        return context
            .FolderLibrary.UpsertRange(folderLibraries)
            .On(fl => new { fl.LibraryId, fl.FolderId })
            .WhenMatched((fls, fli) => new() { LibraryId = fli.LibraryId, FolderId = fli.FolderId })
            .RunAsync();
    }

    public Task<int> UpdateFolderAsync(Folder folder)
    {
        context.Folders.Update(folder);
        return context.SaveChangesAsync();
    }

    public async Task<int> DeleteFolderAsync(Folder folder)
    {
        // The SQLite schema uses DeleteBehavior.Restrict, so a folder's required
        // FK dependents must be deleted before the folder itself. Do it set-based
        // with ExecuteDelete rather than a tracked Remove: GetFolderByIdAsync loads
        // the folder WITH its FolderLibraries navigation, so context.Folders.Remove
        // marks a required relationship severed and throws in the change tracker
        // (HandleConceptualNulls) before any SQL runs — a PRAGMA foreign_keys = OFF
        // toggle can't prevent a tracker-time error. ExecuteDelete bypasses the
        // tracker entirely and emits plain DELETEs in FK-safe order.
        await context
            .FolderLibrary.Where(folderLibrary => folderLibrary.FolderId == folder.Id)
            .ExecuteDeleteAsync();

        await context
            .EncodingPresetFolders.Where(encodingPresetFolder =>
                encodingPresetFolder.FolderId == folder.Id
            )
            .ExecuteDeleteAsync();

        return await context.Folders.Where(f => f.Id == folder.Id).ExecuteDeleteAsync();
    }

    public Task<List<Folder>> GetAllFoldersAsync(CancellationToken ct = default)
    {
        return context.Folders.AsNoTracking().ToListAsync(ct);
    }

    /// <summary>
    /// Replaces the edited library's links to <paramref name="folders"/>.
    /// <para>
    /// Only the libraries named in <paramref name="folderLibraries"/> lose
    /// their links: a folder can belong to several libraries, and editing one
    /// must not take the folder away from the others (#414).
    /// </para>
    /// <para>
    /// The delete and the insert run in one transaction, so an insert that
    /// fails leaves the links as they were instead of leaving the library
    /// with no folders (#415).
    /// </para>
    /// </summary>
    public async Task<int> SyncFolderLibraryAsync(
        FolderLibrary[] folderLibraries,
        List<Folder> folders
    )
    {
        List<Ulid> libraryIds = [.. folderLibraries.Select(link => link.LibraryId).Distinct()];
        List<Ulid> folderIds = [.. folders.Select(folder => folder.Id)];

        await using IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync();

        await context
            .FolderLibrary.Where(link =>
                libraryIds.Contains(link.LibraryId) && folderIds.Contains(link.FolderId)
            )
            .ExecuteDeleteAsync();

        int added = await AddFolderLibraryAsync(folderLibraries);

        await transaction.CommitAsync();

        return added;
    }
}
