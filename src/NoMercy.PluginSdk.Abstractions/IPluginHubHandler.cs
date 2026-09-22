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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// A plugin's side of the hub. Implement it to receive what clients send to
/// <c>plugin:{id}</c>; the platform never invokes it for a plugin that has not
/// declared the <c>ws</c> capability or is not active.
/// </summary>
public interface IPluginHubHandler
{
    Ulid PluginId { get; }

    Task HandleAsync(PluginHubMessage message, IPluginHubClient client, CancellationToken ct);
}
