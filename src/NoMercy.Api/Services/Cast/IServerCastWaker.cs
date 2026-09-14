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

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NoMercy.Api.Services.Music;
using NoMercy.Database.Models.Users;
using NoMercy.Networking;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.Setup;
using NoMercy.Setup.Cast;

namespace NoMercy.Api.Services.Cast;

/// <summary>
/// Wakes a TV from the server, so no client needs a Cast SDK of its own.
/// </summary>
public interface IServerCastWaker
{
    /// <inheritdoc cref="ServerCastWaker.WakeAsync" />
    Task<bool> WakeAsync(Device tv, Guid userId, CastIntent intent);
}
