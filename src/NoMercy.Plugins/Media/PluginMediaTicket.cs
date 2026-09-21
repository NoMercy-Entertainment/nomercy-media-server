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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Media;

/// <summary>What a ticket says once the server has checked it is one of its own.</summary>
/// <param name="Request">
/// The whole request the plugin handed over, not just an address: the headers
/// an upstream wants are part of reaching it, and a ticket that dropped them
/// would fetch a 403 from a provider that was working a moment earlier.
/// </param>
public sealed record PluginMediaTicket(
    Ulid PluginId,
    Guid UserId,
    PluginProxyRequest Request,
    DateTimeOffset ExpiresAt
)
{
    /// <summary>The first address to try, which is the only one a single-link ticket has.</summary>
    public string Upstream => Request.Links[0].Url.ToString();
}
