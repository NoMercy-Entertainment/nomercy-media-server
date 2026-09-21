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

using NoMercy.Events;
using NoMercy.Events.Plugins;

namespace NoMercy.Plugins.Telemetry;

/// <summary>
/// Turns the failure the loader already announces into a number.
/// <para>
/// Subscribed rather than threaded through the loader: the loader's job is to
/// say what happened, and counting it is somebody else's.
/// </para>
/// </summary>
public sealed class PluginCrashListener : IDisposable
{
    private readonly IDisposable _subscription;

    public PluginCrashListener(IEventBus events, IPluginCrashCounter counter)
    {
        _subscription = events.Subscribe<PluginErrorOccurredEvent>(
            (occurred, _) =>
            {
                if (Ulid.TryParse(occurred.PluginId, out Ulid pluginId))
                    counter.RecordCrash(pluginId);

                return Task.CompletedTask;
            }
        );
    }

    public void Dispose() => _subscription.Dispose();
}
