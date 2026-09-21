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

namespace NoMercy.Plugins.Runtime;

/// <summary>
/// Everything one plugin holds outside the process: listening sockets, router
/// mappings, child processes.
/// <para>
/// A plugin that crashed used to leave all three behind it, and the port it
/// bound stayed bound until the server was restarted. The lifecycle drains
/// this at every stop site instead, so a plugin that stops for any reason
/// stops holding things.
/// </para>
/// </summary>
public interface IPluginResourceLedger
{
    void Track(Ulid pluginId, IAsyncDisposable resource);

    void Forget(Ulid pluginId, IAsyncDisposable resource);

    int Held(Ulid pluginId);

    Task ReleaseAsync(Ulid pluginId, CancellationToken ct = default);
}
