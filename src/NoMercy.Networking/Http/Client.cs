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

using Microsoft.AspNetCore.SignalR;
using NoMercy.Database.Models.Users;

namespace NoMercy.Networking.Http;

public class Client : Device
{
    public Guid Sub { get; set; }
    public int? Ping { get; set; }
    public ISingleClientProxy Socket { get; set; } = null!;
    public string Endpoint { get; set; } = string.Empty;
    public bool IsModerator { get; set; }
}
