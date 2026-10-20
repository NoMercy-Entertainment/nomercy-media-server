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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Lifecycle;

namespace NoMercyQueue.Readiness;

/// <summary>
/// Default implementation of <see cref="IServerReadinessGate"/>.
///
/// <para>Workers await <see cref="WaitForReadyAsync"/> at the top of their
/// processing loop (never in StartAsync) so the host finishes booting before
/// any job is dequeued. Additional signals (e.g. hardware benchmark) plug in
/// via <see cref="AddSignal"/> from their own hosted service StartAsync.</para>
///
/// <para>The gate is <em>sealed</em> one async tick after
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/> fires. All
/// <see cref="IHostedService.StartAsync"/> calls complete before that event,
/// so every hosted service that wants to contribute a signal can call
/// <see cref="AddSignal"/> during its own StartAsync without racing against
/// workers that have already snapshotted the signal list.</para>
///
/// <para>A 60-second fallback fires if any signal hangs — queues will proceed
/// with a warning rather than block forever.</para>
/// </summary>
public sealed class ServerReadinessGate : IServerReadinessGate
{
    private readonly ILogger<ServerReadinessGate> _logger;

    // Guards the signal list — AddSignal can be called from multiple threads.
    private readonly object _lock = new();
    private readonly List<(string Name, Task Signal)> _signals = [];

    // Resolved one async tick after ApplicationStarted so that all
    // IHostedService.StartAsync completions (which precede ApplicationStarted)
    // have had a chance to call AddSignal before any waiter snapshots the list.
    private readonly TaskCompletionSource _sealed = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    // Resolved once the first caller has either observed every signal as
    // completed or hit the timeout. All other waiters await the same Task so
    // they unblock together — without this, each worker spawned its own
    // Task.WhenAny over the same signal set, and threadpool starvation could
    // hold one worker's continuation for minutes while another already passed.
    private readonly TaskCompletionSource _resolvedTcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    // 5 minutes — covers a full hardware benchmark sweep on a cold boot
    // (15s grace + up to 30s busy poll + 4-codec × 4-resolution sweep where
    // libsvtav1 4K alone runs ~25s and libaom-av1 1080p can run minutes).
    // Lower values trigger spurious timeouts that release queues with an
    // empty SpeedIndex.
    private const int TimeoutSeconds = 300;

    public ServerReadinessGate(
        IHostApplicationLifetime lifetime,
        ILogger<ServerReadinessGate> logger
    )
    {
        _logger = logger;

        TaskCompletionSource startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStarted.Register(() =>
        {
            startedTcs.TrySetResult();
            // Seal one async tick later — gives any synchronous AddSignal
            // calls that follow ApplicationStarted a chance to register
            // before WaitCoreAsync snapshots the list.
            Task.Run(() => _sealed.TrySetResult());
        });

        // If ApplicationStarted already fired (e.g. re-entrant call in tests)
        // resolve both immediately.
        if (lifetime.ApplicationStarted.IsCancellationRequested)
        {
            startedTcs.TrySetResult();
            _sealed.TrySetResult();
        }

        lock (_lock)
        {
            _signals.Add(("host-started", startedTcs.Task));
        }
    }

    // Single watcher loop kicked off once on first WaitForReadyAsync.
    private int _watcherStarted;

    public void AddSignal(string name, Task signal)
    {
        lock (_lock)
        {
            if (_sealed.Task.IsCompleted)
            {
                // Gate already sealed — caller registered after the snapshot window.
                _logger.LogWarning(
                    "Server readiness: signal '{Name}' added after gate sealed — ignored",
                    name
                );
                return;
            }

            if (_resolvedTcs.Task.IsCompleted)
            {
                // Gate fully resolved — caller registered too late.
                _logger.LogWarning(
                    "Server readiness: signal '{Name}' added after gate resolved — ignored",
                    name
                );
                return;
            }

            _signals.Add((name, signal));
        }
    }

    public Task WaitForReadyAsync(CancellationToken ct)
    {
        // Called directly: it yields at its first wait, and a sealed gate resolves
        // here instead of waiting for a thread-pool thread.
        if (Interlocked.CompareExchange(ref _watcherStarted, 1, 0) == 0)
            _ = WatchAsync(ct);

        return _resolvedTcs.Task.WaitAsync(ct);
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            // Step 1: wait until the registration window closes so the snapshot
            // includes every signal that was added during hosted service startup.
            await _sealed.Task.WaitAsync(ct).ConfigureAwait(false);

            // Step 2: snapshot the now-final list.
            (string Name, Task Signal)[] snapshot;
            lock (_lock)
            {
                snapshot = [.. _signals];
            }

            string names = string.Join(", ", snapshot.Select(s => s.Name));
            _logger.LogInformation("Server readiness: queues waiting for [{Names}]", names);

            Task allReady = Task.WhenAll(snapshot.Select(s => s.Signal));
            Task timeout = Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), ct);

            Task first = await Task.WhenAny(allReady, timeout).ConfigureAwait(false);

            if (first != allReady)
            {
                List<string> laggards = snapshot
                    .Where(s => !s.Signal.IsCompleted)
                    .Select(s => s.Name)
                    .ToList();

                _logger.LogWarning(
                    "Server readiness: {Timeout}s timeout reached — signals still pending: [{Laggards}]. Proceeding anyway",
                    [TimeoutSeconds, string.Join(", ", laggards)]
                );
            }
            else
            {
                _logger.LogInformation("Server readiness: all signals satisfied — queues active");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server readiness watcher failed; releasing gate anyway");
        }
        finally
        {
            _resolvedTcs.TrySetResult();
        }
    }
}
