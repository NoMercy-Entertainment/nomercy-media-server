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
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Libraries;

public class LibraryRepository(MediaContext context, IStorageDriver storageDriver)
    : ILibraryRepository
{
    public async Task<IEnumerable<MediaFolderExtend>> GetRootFoldersAsync(string path)
    {
        await using MediaScan mediaScan = new(storageDriver);
        return
        [
            .. (await mediaScan.DisableRegexFilter().Process(path, 2)).SelectMany(r =>
                r.SubFolders ?? []
            ),
        ];
    }

    public Task<Library?> GetLibraryWithFolders(Ulid id)
    {
        return context
            .Libraries.AsNoTracking()
            .Include(library => library.FolderLibraries)
                .ThenInclude(folderLibrary => folderLibrary.Folder)
            .FirstOrDefaultAsync(library => library.Id == id);
    }

    public Task<Folder?> GetLibraryFolder(Ulid folderId)
    {
        return context
            .Folders.Include(folder => folder.FolderLibraries)
                .ThenInclude(folderLibrary => folderLibrary.Library)
                    .ThenInclude(f => f.FolderLibraries)
                        .ThenInclude(f => f.Folder)
            .Include(folder => folder.EncodingPresetFolders)
                .ThenInclude(link => link.Preset)
            .FirstOrDefaultAsync(folder => folder.Id == folderId);
    }

    public Task<Library?> GetLibraryByIdWithFolders(Ulid libraryId)
    {
        return context
            .Libraries.AsNoTracking()
            .Include(library => library.FolderLibraries)
                .ThenInclude(folderLibrary => folderLibrary.Folder)
            .FirstOrDefaultAsync(library => library.Id == libraryId);
    }

    public async Task<HashSet<string>> GetExistingFolderNamesAsync(
        Ulid libraryId,
        string libraryType
    )
    {
        IEnumerable<string?> folders = libraryType switch
        {
            MediaTypes.MovieMediaType => await context
                .LibraryMovie.Where(lm => lm.LibraryId == libraryId)
                .Include(lm => lm.Movie)
                .Select(lm => lm.Movie.Folder)
                .ToListAsync(),
            MediaTypes.MusicMediaType => await GetAlbumFolderLeafNamesAsync(libraryId),
            _ => await context
                .LibraryTv.Where(lt => lt.LibraryId == libraryId)
                .Include(lt => lt.Tv)
                .Select(lt => lt.Tv.Folder)
                .ToListAsync(),
        };

        return
        [
            .. folders
                .Where(f => f is not null)
                .Select(f => f!.Replace("/", "").NormalizeForComparison()),
        ];
    }

    // Music folders live on Album rows (linked through AlbumLibrary), not LibraryTv.
    // Album.Folder is the path under the library root (e.g. "/Artist/Album"); the scan
    // compares the album folder's own name, so only the last segment is kept.
    private async Task<List<string?>> GetAlbumFolderLeafNamesAsync(Ulid libraryId)
    {
        List<string?> albumFolders = await context
            .AlbumLibrary.Where(al => al.LibraryId == libraryId)
            .Select(al => al.Album.Folder)
            .ToListAsync();

        return
        [
            .. albumFolders.Select(f =>
                f?.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault()
            ),
        ];
    }

    public void Dispose()
    {
        context.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
    }
}
