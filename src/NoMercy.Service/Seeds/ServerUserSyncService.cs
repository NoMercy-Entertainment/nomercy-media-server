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
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Service.Seeds.Dto;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.Service.Seeds;

/// <summary>Outcome of a single <see cref="IServerUserSyncService.SyncAsync"/> run.</summary>
/// <param name="Attempted">False when the sync was skipped (no auth token yet).</param>
/// <param name="UpstreamUserCount">Number of server-users returned by api.nomercy.tv.</param>
/// <param name="RevokedCount">Number of local users whose access was revoked because
/// they are no longer present upstream (removed/declined server-user row).</param>
public readonly record struct ServerUserSyncResult(
    bool Attempted,
    int UpstreamUserCount,
    int RevokedCount
);

public interface IServerUserSyncService
{
    /// <summary>
    /// Pulls the current server-users list from api.nomercy.tv and reconciles it
    /// into the local Users table: upserts every accepted upstream user (an
    /// invite accepted after this server's first boot lands here too, not just
    /// once on an empty database), and revokes local access for any non-owner
    /// user no longer present upstream. Safe to call repeatedly — every call is
    /// a full reconciliation pass, not a one-shot seed.
    /// </summary>
    Task<ServerUserSyncResult> SyncAsync(
        MediaContext dbContext,
        IStorage storage,
        string? accessToken,
        CancellationToken cancellationToken = default
    );
}

public class ServerUserSyncService(IServerUserApiClient apiClient) : IServerUserSyncService
{
    public async Task<ServerUserSyncResult> SyncAsync(
        MediaContext dbContext,
        IStorage storage,
        string? accessToken,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            Logger.Setup(
                "Skipping server-user sync — no auth token (will retry via DegradedModeRecovery / next scheduled sync)",
                LogEventLevel.Warning
            );
            return new(false, 0, 0);
        }

        ServerUserDtoData[] serverUsers;
        try
        {
            serverUsers = await apiClient.GetServerUsersAsync(accessToken, cancellationToken);
        }
        catch (HttpRequestException ex)
            when (ex.Message.Contains("400") || ex.Message.Contains("403"))
        {
            // The API answers 400 "not allowed" when it has no server row owned by
            // the authenticated account for this device id — the server row belongs
            // to a DIFFERENT account (or assignment never completed). Retrying on a
            // cron cannot fix that; only re-running setup/assignment can. Say so
            // instead of spamming the raw envelope every sync interval.
            Logger.Setup(
                "Server-user sync rejected by the API: this server is not registered to the "
                    + "authenticated account (ownership mismatch or incomplete assignment). "
                    + "Re-run setup to re-assign the server. Raw: "
                    + ex.Message,
                LogEventLevel.Error
            );
            return new(false, 0, 0);
        }

        Logger.Setup($"Found {serverUsers.Length} server users", LogEventLevel.Verbose);

        User[] incomingUsers = serverUsers
            // Skip rows whose UserId can't be parsed — a single bad row from the
            // upstream API used to abort the whole sync via FormatException,
            // leaving the server with no users at all.
            .Where(serverUser => Guid.TryParse(serverUser.UserId, out _))
            .Select(serverUser => new User
            {
                Id = Guid.Parse(serverUser.UserId),
                Email = serverUser.Email,
                Name = serverUser.Name,
                Allowed = true,
                AudioTranscoding = serverUser.Enabled,
                NoTranscoding = serverUser.Enabled,
                VideoTranscoding = serverUser.Enabled,
                Owner = serverUser.IsOwner,
            })
            .ToArray();

        // Self floor check (defense in depth against a 200-with-bad-shape response
        // that parses "successfully" into an empty/partial list, e.g. `{"data":[]}`
        // or a truncated array). The request always sends with_self=true, so any
        // genuinely authoritative response contains this server's own local owner.
        // If it doesn't — including the empty-list case — the payload is untrustworthy;
        // abort the whole reconcile (no upsert, no revoke) rather than let
        // RevokeStaleUsersAsync read "owner is the only survivor" as ground truth.
        // No local owner yet (pre-registration) means nothing is stale to protect,
        // so the check is skipped rather than blocking a legitimate first sync.
        Guid? localOwnerId = await dbContext
            .Users.Where(u => u.Owner)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (localOwnerId.HasValue && incomingUsers.All(u => u.Id != localOwnerId.Value))
        {
            Logger.Setup(
                $"Server-user sync aborted — response ({incomingUsers.Length} user(s)) did not include the local owner despite with_self=true; treating as untrustworthy and skipping this cycle without any changes",
                LogEventLevel.Warning
            );
            return new(false, incomingUsers.Length, 0);
        }

        await dbContext
            .Users.UpsertRange(incomingUsers)
            .On(v => new { v.Id })
            .WhenMatched(
                (existing, incoming) =>
                    new()
                    {
                        Id = incoming.Id,
                        Email = incoming.Email,
                        Name = incoming.Name,
                        Allowed = incoming.Allowed,
                        Manage = existing.Manage,
                        // Transcoding flags are local per-user config, not upstream
                        // membership state — re-stomping them from serverUser.Enabled
                        // every cycle would silently undo a local override every 5
                        // minutes. Only an INSERT (new user, no `existing` row) gets
                        // the serverUser.Enabled-derived defaults from `incoming`.
                        AudioTranscoding = existing.AudioTranscoding,
                        NoTranscoding = existing.NoTranscoding,
                        VideoTranscoding = existing.VideoTranscoding,
                        Owner = incoming.Owner,
                    }
            )
            .RunAsync(cancellationToken);

        int revokedCount = await RevokeStaleUsersAsync(dbContext, incomingUsers, cancellationToken);

        await SeedLibraryAccessAsync(dbContext, storage, incomingUsers, cancellationToken);

        return new(true, incomingUsers.Length, revokedCount);
    }

    /// <summary>
    /// A non-owner local user who is currently Allowed but no longer appears in the
    /// upstream server-users list (declined, removed by the owner, or soft-deleted
    /// on nomercy-tv) loses local access. The owner row is never touched here.
    /// </summary>
    private static async Task<int> RevokeStaleUsersAsync(
        MediaContext dbContext,
        User[] incomingUsers,
        CancellationToken cancellationToken
    )
    {
        HashSet<Guid> incomingIds = incomingUsers.Select(u => u.Id).ToHashSet();

        List<User> allowedNonOwners = await dbContext
            .Users.Where(u => u.Allowed && !u.Owner)
            .ToListAsync(cancellationToken);

        List<User> toRevoke = allowedNonOwners.Where(u => !incomingIds.Contains(u.Id)).ToList();

        if (toRevoke.Count == 0)
            return 0;

        foreach (User user in toRevoke)
        {
            user.Allowed = false;
            user.AudioTranscoding = false;
            user.NoTranscoding = false;
            user.VideoTranscoding = false;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        Logger.Setup(
            $"Revoked local access for {toRevoke.Count} user(s) no longer present upstream",
            LogEventLevel.Information
        );

        return toRevoke.Count;
    }

    private static async Task SeedLibraryAccessAsync(
        MediaContext dbContext,
        IStorage storage,
        User[] users,
        CancellationToken cancellationToken
    )
    {
        if (users.Length == 0 || !storage.Exists(AppFiles.LibrariesSeedFile))
            return;

        string librariesJson = await storage.ReadAllTextAsync(
            AppFiles.LibrariesSeedFile,
            cancellationToken
        );
        Library[] libraries = librariesJson.FromJson<Library[]>() ?? [];

        if (libraries.Length == 0)
            return;

        foreach (User user in users)
        {
            List<LibraryUser> libraryUsers = libraries
                .Select(library => new LibraryUser { LibraryId = library.Id, UserId = user.Id })
                .ToList();

            await dbContext
                .LibraryUser.UpsertRange(libraryUsers)
                .On(v => new { v.LibraryId, v.UserId })
                .WhenMatched((lus, lui) => new() { LibraryId = lui.LibraryId, UserId = lui.UserId })
                .RunAsync(cancellationToken);
        }
    }
}
