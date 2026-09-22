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
/// Server events by topic, and the plugin's own events other plugins may hear.
/// A topic list rather than the host bus: the bus carried every event the server
/// raises, including ones about users this plugin has no capability for.
/// </summary>
public interface IPluginEvents
{
    void Subscribe<T>(string topic, Func<T, CancellationToken, Task> handler);

    Task PublishAsync<T>(string name, T payload, CancellationToken ct = default);
}
