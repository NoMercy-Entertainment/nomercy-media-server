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

namespace NoMercy.Plugins.Abstractions;

/// <summary>A long-running task the host starts, watches and restarts.</summary>
public sealed record PluginWorker
{
    public required string Name { get; init; }

    /// <summary>A key, not a sentence: the owner reads this in their language.</summary>
    public required string LabelKey { get; init; }

    public required Func<IPluginContext, CancellationToken, Task> RunAsync { get; init; }

    public bool RestartOnCrash { get; init; } = true;

    /// <summary>
    /// Past this, the host stops restarting and disables the worker. A worker
    /// that crashes on startup would otherwise be restarted for ever, and the
    /// loop costs more than the worker was doing.
    /// </summary>
    public int MaxRestartsPerHour { get; init; } = 3;
}
