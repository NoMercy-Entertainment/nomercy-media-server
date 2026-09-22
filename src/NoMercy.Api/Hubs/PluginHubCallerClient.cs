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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.Hubs;

/// <summary>
/// The single connection that sent a message, handed to the plugin so it can
/// answer without broadcasting. The plugin id travels in the envelope because
/// one connection is subscribed to several plugins at once.
/// </summary>
public class PluginHubCallerClient(IClientProxy caller, Ulid pluginId) : IPluginHubClient
{
    public Task SendAsync(string type, object? payload) =>
        caller.SendAsync(
            "PluginMessage",
            new
            {
                pluginId,
                type,
                payload,
            }
        );
}
