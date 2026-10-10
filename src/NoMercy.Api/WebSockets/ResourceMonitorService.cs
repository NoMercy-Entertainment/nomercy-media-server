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
using NoMercy.Monitoring;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.WebSockets;

public class ResourceMonitorService(
    ILogger<ResourceMonitorService> logger,
    ResourceMonitor resourceMonitor,
    IClientMessenger clientMessenger
) : IResourceMonitorService
{
    private const int BroadcastIntervalMs = 1000;

    private readonly Lock _sync = new();
    private bool _broadcasting;
    private CancellationTokenSource? _cancellationTokenSource;

    public int ActiveSubscribers => _broadcasting ? 1 : 0;

    public void Start()
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_broadcasting)
                return;
            _broadcasting = true;
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new();
            token = _cancellationTokenSource.Token;
        }

        logger.LogInformation("Starting resource monitoring broadcast");
        _ = Task.Run(async () =>
        {
            try
            {
                await BroadcastLoop(token);
            }
            catch (Exception ex)
            {
                logger.LogError("Resource monitor broadcast loop crashed: {Message}", ex.Message);
            }
        });
    }

    public void Stop()
    {
        CancellationTokenSource? source;
        lock (_sync)
        {
            if (!_broadcasting)
                return;
            _broadcasting = false;
            source = _cancellationTokenSource;
        }

        logger.LogInformation("Stopping resource monitoring broadcast");
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS already disposed — nothing to cancel.
        }
    }

    private async Task BroadcastLoop(CancellationToken cancellationToken)
    {
        while (_broadcasting && !cancellationToken.IsCancellationRequested)
        {
            DateTime time = DateTime.Now;
            bool failed = false;
            try
            {
                Resource resourceData = resourceMonitor.Monitor();
                await clientMessenger.SendToAll("ResourceUpdate", "dashboardHub", resourceData);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError("Error broadcasting resource data: {Message}", e.Message);
                failed = true;
            }

            // The delay must run on the error path too. It previously lived inside
            // the try, so a persistent Monitor()/send failure skipped it and the
            // loop respun at CPU speed, pegging a core while a socket stayed wedged.
            int delay = NextDelayMs(
                failed,
                (int)(DateTime.Now - time).TotalMilliseconds,
                BroadcastIntervalMs
            );
            if (delay > 0)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Milliseconds to wait before the next broadcast iteration. On failure the
    /// full interval is always used (a backoff, so a persistent error can't
    /// hot-spin the loop); on success the interval is paced down by how long the
    /// iteration already took (a non-positive result means the work outran the
    /// interval, so the loop should continue immediately).
    /// </summary>
    public static int NextDelayMs(bool failed, int elapsedMs, int intervalMs) =>
        failed ? intervalMs : intervalMs - elapsedMs;
}
