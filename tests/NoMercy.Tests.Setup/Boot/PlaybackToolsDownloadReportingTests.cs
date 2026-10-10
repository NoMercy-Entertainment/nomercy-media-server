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

using System.Reflection;
using NoMercy.Events;
using NoMercy.Events.Playback;
using NoMercy.NmSystem.Auth;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;

namespace NoMercy.Tests.Setup.Boot;

[Trait("Category", "Unit")]
public sealed class PlaybackToolsDownloadReportingTests : IDisposable
{
    public void Dispose()
    {
        SetTasks([]);
        Start.IsDegradedMode = false;
    }

    private static void SetTasks(List<StartupTask> tasks)
    {
        FieldInfo field = typeof(Start).GetField(
            "_allTasks",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        field.SetValue(null, tasks);
    }

    [Fact]
    public async Task InitRemaining_BinariesDeferred_PublishesFirstFailureWithReason()
    {
        InMemoryEventBus bus = new();
        EventBusProvider.Configure(bus);
        List<PlaybackToolsDownloadFailedEvent> failures = [];
        using IDisposable subscription = bus.Subscribe<PlaybackToolsDownloadFailedEvent>(
            (failure, _) =>
            {
                failures.Add(failure);
                return Task.CompletedTask;
            }
        );
        SetTasks([
            new(
                "Binaries",
                () => throw new InvalidOperationException("release feed unavailable"),
                true,
                2
            ),
        ]);

        await Start.InitRemaining();

        PlaybackToolsDownloadFailedEvent failure = Assert.Single(failures);
        Assert.Equal("release feed unavailable", failure.ErrorMessage);
        Assert.Equal(1, failure.Attempt);
        Assert.True(failure.NextRetryAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RetryFails_PublishesAttemptTwoWithReason()
    {
        InMemoryEventBus bus = new();
        List<PlaybackToolsDownloadFailedEvent> failures = [];
        DeferredTasks tasks = new()
        {
            ApiKeysLoaded = true,
            Authenticated = true,
            NetworkDiscovered = true,
            SeedsRun = true,
            Registered = true,
        };
        using IDisposable subscription = bus.Subscribe<PlaybackToolsDownloadFailedEvent>(
            (failure, _) =>
            {
                failures.Add(failure);
                tasks.AllCompleted = true;
                return Task.CompletedTask;
            }
        );
        DegradedModeRecovery recovery = new(
            Mock.Of<IAuthTokenStore>(),
            Mock.Of<IApiKeyLoader>(),
            Mock.Of<IApiKeyStore>(),
            Mock.Of<IServerRegistrationService>(),
            networkDiscovery: null,
            delay: _ => Task.CompletedTask,
            eventBus: bus,
            checkConnectivity: () => Task.FromResult(true),
            binaryExists: () => false,
            downloadBinary: () => throw new InvalidOperationException("download timed out")
        );

        // The loop only ends once the failure event arrives. Bound the wait so a
        // regression that stops publishing fails the test instead of hanging it.
        try
        {
            // Task.Run: with instant delays the loop completes every step
            // synchronously, so only a separate thread lets the timeout fire.
            await Task.Run(() => recovery.StartRecoveryLoop(tasks))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            tasks.AllCompleted = true;
        }

        PlaybackToolsDownloadFailedEvent failure = Assert.Single(failures);
        Assert.Equal(2, failure.Attempt);
        Assert.Equal("download timed out", failure.ErrorMessage);
        Assert.False(tasks.BinariesReady);
    }

    [Fact]
    public async Task RetrySucceedsAfterFailure_PublishesRecoveredOnce()
    {
        InMemoryEventBus bus = new();
        List<PlaybackToolsReadyEvent> recovered = [];
        using IDisposable subscription = bus.Subscribe<PlaybackToolsReadyEvent>(
            (ready, _) =>
            {
                recovered.Add(ready);
                return Task.CompletedTask;
            }
        );
        DeferredTasks tasks = new();
        bool binaryExists = false;

        await DegradedModeRecovery.TryProvisionBinariesAsync(
            tasks,
            attempt: 2,
            eventBus: bus,
            binaryExists: () => binaryExists,
            download: () =>
            {
                binaryExists = true;
                return Task.CompletedTask;
            }
        );
        await DegradedModeRecovery.TryProvisionBinariesAsync(
            tasks,
            attempt: 3,
            eventBus: bus,
            binaryExists: () => true
        );

        Assert.True(tasks.BinariesReady);
        Assert.Equal(2, Assert.Single(recovered).Attempt);
    }

    [Fact]
    public async Task RetryWithFfmpegPresent_StillRetriesOtherFailedDownloads()
    {
        DeferredTasks tasks = new();
        int attempts = 0;

        await DegradedModeRecovery.TryProvisionBinariesAsync(
            tasks,
            binaryExists: () => true,
            download: () =>
            {
                attempts++;
                return Task.CompletedTask;
            }
        );

        Assert.Equal(1, attempts);
        Assert.True(tasks.BinariesReady);
    }
}
