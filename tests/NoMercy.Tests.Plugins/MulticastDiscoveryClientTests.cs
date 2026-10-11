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

using System.Collections.Concurrent;
using System.Reflection;
using NoMercy.PluginSdk.Network;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class MulticastDiscoveryClientTests
{
    [Fact]
    public async Task Stop_after_reannouncing_does_not_repeat_the_stale_port()
    {
        ConcurrentQueue<int> ports = new();
        TaskCompletionSource firstRepeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource latestRepeat = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Send(byte[] message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MulticastDnsRecord service = Assert.Single(
                MulticastDnsMessage.Read(message),
                record => record.Type == MulticastDnsMessage.TypeSrv
            );
            int port = MulticastDnsMessage.ReadService(message, service).Port;
            ports.Enqueue(port);

            if (port == 8080 && ports.Count(item => item == 8080) >= 2)
                firstRepeat.TrySetResult();
            if (port == 9090 && ports.Count(item => item == 9090) >= 2)
                latestRepeat.TrySetResult();

            return Task.CompletedTask;
        }

        await using MulticastDiscoveryClient client = new(Send, TimeSpan.FromMilliseconds(10));
        await client.AnnounceAsync("_bittorrent._tcp", "nomercy", 8080, null);
        await firstRepeat.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await client.AnnounceAsync("_bittorrent._tcp", "nomercy", 9090, null);
        await latestRepeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.StopAsync("_bittorrent._tcp", "nomercy");

        int staleBeforeWait = ports.Count(port => port == 8080);
        await Task.Delay(100);

        Assert.Equal(staleBeforeWait, ports.Count(port => port == 8080));
    }

    [Fact]
    public async Task Reannouncing_the_same_service_cancels_the_previous_repeat_loop()
    {
        await using MulticastDiscoveryClient client = new();
        ConcurrentDictionary<string, CancellationTokenSource> announcing = Announcing(client);

        await client.AnnounceAsync("_bittorrent._tcp", "nomercy", 8080, null);
        CancellationToken previous = announcing["_bittorrent._tcp/nomercy"].Token;

        await client.AnnounceAsync("_bittorrent._tcp", "nomercy", 9090, null);

        Assert.True(previous.IsCancellationRequested);
        Assert.Single(announcing);
        Assert.False(announcing["_bittorrent._tcp/nomercy"].IsCancellationRequested);

        await client.StopAsync("_bittorrent._tcp", "nomercy");

        Assert.Empty(announcing);
    }

    private static ConcurrentDictionary<string, CancellationTokenSource> Announcing(
        MulticastDiscoveryClient client
    )
    {
        FieldInfo field = typeof(MulticastDiscoveryClient).GetField(
            "_announcing",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return (ConcurrentDictionary<string, CancellationTokenSource>)field.GetValue(client)!;
    }
}
