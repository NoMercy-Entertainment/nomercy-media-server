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
using NoMercy.Api.DTOs.Media;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Services;

public class SetupService
{
    private readonly MediaContext _mediaContext;

    public SetupService(MediaContext mediaContext)
    {
        _mediaContext = mediaContext;
    }

    public Task<List<Library>> GetSetupLibraries(Guid userId)
    {
        return _mediaContext
            .Libraries.AsNoTracking()
            .Where(library => library.LibraryUsers.Any(u => u.UserId == userId))
            .Include(library => library.FolderLibraries)
                .ThenInclude(fl => fl.Folder)
                    .ThenInclude(f => f.EncodingPresetFolders)
                        .ThenInclude(link => link.Preset)
            .Include(library => library.LanguageLibraries)
                .ThenInclude(ll => ll.Language)
            .Include(library => library.LibraryMovies)
            .Include(library => library.LibraryTvs)
            .OrderBy(library => library.Order)
            .ToListAsync();
    }

    public Task<List<Playlist>> GetSetupPlaylistsAsync(Guid userId)
    {
        return _mediaContext
            .Playlists.AsNoTracking()
            .Where(playlist => playlist.UserId == userId)
            .ToListAsync();
    }
}
