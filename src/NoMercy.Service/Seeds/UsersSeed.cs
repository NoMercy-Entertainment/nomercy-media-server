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
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.Service.Seeds;

public static class UsersSeed
{
    /// <summary>
    /// Seeds users on first boot and repairs missing default-library grants on
    /// later boots. Ongoing user reconciliation is handled by nomercy-tv's
    /// PushServerUserJob.
    /// </summary>
    public static async Task Init(
        this MediaContext dbContext,
        IStorage storage,
        string? accessToken,
        IServerUserSyncService? syncService = null
    )
    {
        try
        {
            bool hasUsers = await dbContext.Users.AnyAsync();
            if (hasUsers)
            {
                await RepairMissingLibraryAccessAsync(dbContext, storage);
                return;
            }

            Logger.Setup("Adding Users", LogEventLevel.Verbose);

            IServerUserSyncService service =
                syncService ?? new ServerUserSyncService(new ServerUserApiClient());

            await service.SyncAsync(dbContext, storage, accessToken);
        }
        catch (Exception e)
        {
            Logger.Setup($"Users seed failed: {e.Message}", LogEventLevel.Warning);
        }
    }

    private static async Task RepairMissingLibraryAccessAsync(
        MediaContext dbContext,
        IStorage storage
    )
    {
        if (!storage.Exists(AppFiles.LibrariesSeedFile))
            return;

        string librariesJson = await storage.ReadAllTextAsync(
            AppFiles.LibrariesSeedFile,
            CancellationToken.None
        );
        Library[] seededLibraries = librariesJson.FromJson<Library[]>() ?? [];
        if (seededLibraries.Length == 0)
            return;

        HashSet<Ulid> seededLibraryIds = seededLibraries.Select(library => library.Id).ToHashSet();
        List<Ulid> libraryIds = await dbContext
            .Libraries.AsNoTracking()
            .Select(library => library.Id)
            .ToListAsync();
        libraryIds = libraryIds.Where(seededLibraryIds.Contains).ToList();
        if (libraryIds.Count == 0)
            return;

        List<Guid> userIds = await dbContext
            .Users.AsNoTracking()
            .Where(user => (user.Allowed || user.Owner) && !user.LibraryUser.Any())
            .Select(user => user.Id)
            .ToListAsync();
        if (userIds.Count == 0)
            return;

        foreach (Guid userId in userIds)
        {
            foreach (Ulid libraryId in libraryIds)
                dbContext.LibraryUser.Add(new(libraryId, userId));
        }

        await dbContext.SaveChangesAsync();
        Logger.Setup($"Repaired default library access for {userIds.Count} user(s)");
    }
}
