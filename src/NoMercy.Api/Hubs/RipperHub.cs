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
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Authorization;
using NoMercy.Data.Activity;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Extensions;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Sources;

namespace NoMercy.Api.Hubs;

public class RipperHub : ConnectionHub
{
    private readonly IDriveMonitor _driveMonitor;
    private readonly DiscSourceFactory _discSourceFactory;

    private readonly ILogger<RipperHub> _logger;

    public RipperHub(
        ILogger<RipperHub> logger,
        IHttpContextAccessor httpContextAccessor,
        IDbContextFactory<MediaContext> contextFactory,
        ConnectedClients connectedClients,
        IActivityLogger activityLogger,
        IDriveMonitor driveMonitor,
        DiscSourceFactory discSourceFactory
    )
        : base(httpContextAccessor, contextFactory, connectedClients, activityLogger)
    {
        _logger = logger;
        _driveMonitor = driveMonitor;
        _discSourceFactory = discSourceFactory;
    }

    public override async Task OnConnectedAsync()
    {
        User user = UserCacheService.GetUser(Context.User.UserId())!;

        await base.OnConnectedAsync();
        _logger.LogDebug("Ripper client connected");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
        _logger.LogDebug("Ripper client disconnected");
    }

    /// <summary>
    /// Returns the current state of the drive at <paramref name="drivePath"/>.
    /// When a disc is inserted and a reader is registered for the disc type,
    /// a lightweight probe is run and the result is returned. For drives with
    /// no disc, or disc types without a registered source, only drive-level
    /// info is returned.
    /// </summary>
    public async Task<object?> GetDriveState(string drivePath)
    {
        if (!AuthPolicy.IsModerator(Context.User))
            return null;

        if (string.IsNullOrWhiteSpace(drivePath))
            return null;

        DiscDrive? drive = _driveMonitor
            .GetDrives()
            .FirstOrDefault(d =>
                d.Path.TrimEnd('\\', '/')
                    .Equals(drivePath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            );

        if (drive is null)
            return null;

        if (!drive.HasDisc)
        {
            return new
            {
                path = drive.Path.TrimEnd('\\', '/'),
                label = drive.Label.OrEmpty(),
                open = true,
                has_disc = false,
                disc_type = drive.DiscType.ToString().ToLowerInvariant(),
            };
        }

        IDiscSource? source = _discSourceFactory.CreateFor(drive.DiscType);
        if (source is null)
        {
            return new
            {
                path = drive.Path.TrimEnd('\\', '/'),
                label = drive.Label.OrEmpty(),
                open = false,
                has_disc = true,
                disc_type = drive.DiscType.ToString().ToLowerInvariant(),
            };
        }

        try
        {
            DiscInfo info = await source.ProbeAsync(drive, CancellationToken.None);
            return new
            {
                path = drive.Path.TrimEnd('\\', '/'),
                label = (info.DiscTitle ?? info.DiscLabel ?? drive.Label).OrEmpty(),
                open = false,
                has_disc = true,
                disc_type = drive.DiscType.ToString().ToLowerInvariant(),
                disc = info,
            };
        }
        catch
        {
            return new
            {
                path = drive.Path.TrimEnd('\\', '/'),
                label = drive.Label.OrEmpty(),
                open = false,
                has_disc = true,
                disc_type = drive.DiscType.ToString().ToLowerInvariant(),
            };
        }
    }
}
