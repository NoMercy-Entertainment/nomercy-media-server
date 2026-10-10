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
using Moq;
using NoMercy.Api.Services.Video;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Api.Services.Video;

/// <summary>
/// The continue-watching carousel is built from watch progress, so a progress
/// write has to tell clients that row is stale. Without the event the carousel
/// only caught up on a full page reload: a title started on one device kept
/// showing its old position — or stayed missing entirely — everywhere else.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ContinueWatchingRefreshTests
{
    private static VideoPlaybackService Build(InMemoryEventBus bus)
    {
        ServiceCollection services = new();
        ServiceProvider provider = services.BuildServiceProvider();

        return new(
            new VideoPlayerStateManager(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IClientMessenger>(),
            bus
        );
    }

    private static VideoPlayerState StateWithoutPlayableItem() => new() { Time = 5000 };

    /// <summary>
    /// Guards the wiring: the refresh must carry the exact key the clients watch,
    /// which is the same one TvShowsController publishes after a watch toggle.
    /// </summary>
    [Fact]
    public async Task ContinueWatchingRefresh_CarriesTheQueryKeyClientsSubscribeTo()
    {
        InMemoryEventBus bus = new();
        List<LibraryRefreshedEvent> received = [];
        bus.Subscribe<LibraryRefreshedEvent>(
            (e, _) =>
            {
                received.Add(e);
                return Task.CompletedTask;
            }
        );

        await bus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["continue-watching"] });

        Assert.Single(received);
        Assert.Equal(["continue-watching"], received[0].QueryKey);
    }

    /// <summary>
    /// The refresh hangs off a successful progression write, so the early-outs must
    /// still short-circuit: no current item means nothing was persisted and there is
    /// nothing to invalidate.
    /// </summary>
    [Fact]
    public async Task NoProgressionWritten_PublishesNothing()
    {
        InMemoryEventBus bus = new();
        List<LibraryRefreshedEvent> received = [];
        bus.Subscribe<LibraryRefreshedEvent>(
            (e, _) =>
            {
                received.Add(e);
                return Task.CompletedTask;
            }
        );

        VideoPlaybackService service = Build(bus);

        await service.StoreWatchProgression(StateWithoutPlayableItem(), new User());

        Assert.Empty(received);
    }
}
