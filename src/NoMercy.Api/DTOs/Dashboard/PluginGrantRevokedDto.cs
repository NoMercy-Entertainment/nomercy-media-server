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

using Newtonsoft.Json;
using NoMercy.Api.DTOs.Common;

namespace NoMercy.Api.DTOs.Dashboard;

/// <summary>The usual status answer to a revoke, and whether it waits for a restart.</summary>
public record PluginGrantRevokedDto : StatusResponseDto<string>
{
    /// <summary>
    /// True when the kind is read only when the plugin starts, so the running
    /// plugin keeps it until it restarts. The server does not restart it.
    /// </summary>
    [JsonProperty("restart_required")]
    public bool RestartRequired { get; init; }
}
