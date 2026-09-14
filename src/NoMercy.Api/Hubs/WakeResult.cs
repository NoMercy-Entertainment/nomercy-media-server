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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Api.Services.Cast;
using NoMercy.Api.WebSockets;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Users;
using NoMercy.Encoder.Devices;
using NoMercy.Networking;
using NoMercy.Networking.Devices;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;
using NoMercy.Setup.Cast;

namespace NoMercy.Api.Hubs;

public sealed record WakeResult([property: JsonProperty("status")] string Status);
