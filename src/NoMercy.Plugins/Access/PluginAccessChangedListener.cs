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

namespace NoMercy.Plugins.Access;

/// <summary>
/// Turns "something changed" into one answer per account.
/// <para>
/// Every gate that can change who may open a plugin raises the same event, so
/// there is one place that decides what to send rather than five that could
/// each forget a case.
/// </para>
/// </summary>
public sealed class PluginAccessChangedListener : IDisposable
{
    private readonly IDisposable _subscription;

    public PluginAccessChangedListener(IEventBus events, PluginAccessNotifier notifier)
    {
        _subscription = events.Subscribe<PluginAccessChangedEvent>(
            (changed, _) =>
            {
                if (Ulid.TryParse(changed.PluginId, out Ulid pluginId))
                    notifier.AccessMayHaveChanged(pluginId);
                else
                    notifier.EverythingMayHaveChanged();

                return Task.CompletedTask;
            }
        );
    }

    public void Dispose() => _subscription.Dispose();
}
