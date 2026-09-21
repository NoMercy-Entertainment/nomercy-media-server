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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Hub;

namespace NoMercy.Api.Hubs;

public class PluginHubContextFactory(IHubContext<PluginHub> hubContext, IPluginHubRouter router)
    : IPluginHubContextFactory
{
    public IPluginHubContext For(Ulid pluginId) =>
        new PluginHubBroadcaster(hubContext, pluginId, router);
}
