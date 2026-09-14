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
using NoMercy.Database;
using NoMercy.Database.Models.Encoder;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;

namespace NoMercy.Data.Repositories;

/// <inheritdoc cref="IIncompleteEncodeRepository"/>
public class IncompleteEncodeRepository(MediaContext context) : IIncompleteEncodeRepository
{
    public Task<List<IncompleteEncode>> GetAllAsync()
    {
        return context
            .IncompleteEncodes.AsNoTracking()
            .OrderByDescending(row => row.LastSeenAt)
            .ToListAsync();
    }

    public Task<IncompleteEncode?> FindAsync(int id) =>
        context.IncompleteEncodes.FindAsync(id).AsTask();

    public async Task RemoveAsync(IncompleteEncode row)
    {
        context.IncompleteEncodes.Remove(row);
        await context.SaveChangesAsync();
    }

    public Task<int> RemoveAllAsync() => context.IncompleteEncodes.ExecuteDeleteAsync();

    public Task<FolderLibrary?> FindFolderLibraryAsync(Ulid folderId)
    {
        return context
            .FolderLibrary.AsNoTracking()
            .FirstOrDefaultAsync(folderLibrary => folderLibrary.FolderId == folderId);
    }

    public Task<VideoFile?> FindVideoFileForMediaAsync(int mediaId)
    {
        return context
            .VideoFiles.AsNoTracking()
            .FirstOrDefaultAsync(videoFile =>
                videoFile.MovieId == mediaId || videoFile.EpisodeId == mediaId
            );
    }
}
