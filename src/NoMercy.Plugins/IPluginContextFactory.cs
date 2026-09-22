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

using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk;

/// <summary>
/// Builds the context handed to one plugin.
/// <para>
/// Assembling it is where the trust decisions are made — which libraries the
/// plugin may write to, which hosts it may reach, whether it gets a writer at
/// all — and those decisions belong in one place. Threading the pieces through
/// every construction site is how one of them ends up passing the wrong plugin
/// id and a plugin reads another's secrets.
/// </para>
/// </summary>
public interface IPluginContextFactory
{
    IPluginContext Create(
        Ulid pluginId,
        string dataFolderPath,
        ILogger logger,
        PluginCapabilities? capabilities,
        string? pluginName = null,
        Version? pluginVersion = null
    );
}
