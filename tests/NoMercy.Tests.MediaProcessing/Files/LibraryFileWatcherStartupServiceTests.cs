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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>The file watcher of every library starts when the host starts.</summary>
[Trait("Category", "Unit")]
public sealed class LibraryFileWatcherStartupServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class RecordingStarter : ILibraryFileWatcherStarter
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public int StartCount;

        public Task Started => _started.Task;

        public void Start()
        {
            Interlocked.Increment(ref StartCount);
            _started.TrySetResult();
        }
    }

    private sealed class ThrowingStarter : ILibraryFileWatcherStarter
    {
        public void Start() => throw new InvalidOperationException("watch failed");
    }

    [Fact]
    public async Task The_watcher_starts_once_when_the_host_starts()
    {
        RecordingStarter starter = new();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddLibraryFileWatcher();
        builder.Services.Replace(ServiceDescriptor.Singleton<ILibraryFileWatcherStarter>(starter));

        using IHost host = builder.Build();
        await host.StartAsync();

        Task finished = await Task.WhenAny(starter.Started, Task.Delay(Wait));
        await host.StopAsync();

        finished.Should().BeSameAs(starter.Started, "the watcher must start with the host");
        starter.StartCount.Should().Be(1);
    }

    [Fact]
    public async Task A_failing_watcher_does_not_stop_the_host()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddLibraryFileWatcher();
        builder.Services.Replace(
            ServiceDescriptor.Singleton<ILibraryFileWatcherStarter>(new ThrowingStarter())
        );

        using IHost host = builder.Build();
        Func<Task> start = async () =>
        {
            await host.StartAsync();
            await Task.Delay(200);
            await host.StopAsync();
        };

        await start.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_service_waits_for_the_application_started_signal()
    {
        RecordingStarter starter = new();
        Mock<IHostApplicationLifetime> lifetime = new();
        using CancellationTokenSource applicationStarted = new();
        lifetime.SetupGet(l => l.ApplicationStarted).Returns(applicationStarted.Token);

        LibraryFileWatcherStartupService service = new(
            lifetime.Object,
            starter,
            NullLogger<LibraryFileWatcherStartupService>.Instance
        );

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        starter.StartCount.Should().Be(0);

        await applicationStarted.CancelAsync();
        Task finished = await Task.WhenAny(starter.Started, Task.Delay(Wait));
        await service.StopAsync(CancellationToken.None);

        finished.Should().BeSameAs(starter.Started);
        starter.StartCount.Should().Be(1);
    }
}
