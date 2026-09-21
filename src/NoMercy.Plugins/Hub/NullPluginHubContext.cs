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

using System.Text.Json.Nodes;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Hub;

/// <summary>
/// Pushing succeeds and reaches nobody, which is the truth where no hub is
/// mapped. The alternative is a plugin crashing outside the web host for
/// calling a method that is part of its contract.
/// </summary>
public class NullPluginHubContext : IPluginHubContext
{
    public Task PushAsync(string type, object? payload) => Task.CompletedTask;

    public Task PushToUserAsync(string userId, string type, object? payload) => Task.CompletedTask;

    /// <summary>
    /// Registering succeeds and nothing will ever call it, for the same reason
    /// pushing reaches nobody: outside the web host there is no hub to route a
    /// client's call through, and refusing here would break a plugin that
    /// registers its handlers in Initialize.
    /// </summary>
    public void Handle(
        string method,
        Func<PluginCaller, JsonNode?, CancellationToken, Task<object?>> handler
    ) { }
}
