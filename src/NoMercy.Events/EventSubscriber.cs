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

namespace NoMercy.Events;

/// <summary>
/// Base for a class that subscribes to <see cref="IEventBus"/> events for its whole
/// lifetime. Pass each subscription to <see cref="Track"/>; disposing the subscriber
/// ends all of them.
/// </summary>
public abstract class EventSubscriber : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];

    protected void Track(IDisposable subscription) => _subscriptions.Add(subscription);

    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();

        _subscriptions.Clear();
        GC.SuppressFinalize(this);
    }
}
