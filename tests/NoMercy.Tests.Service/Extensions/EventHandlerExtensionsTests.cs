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
using NoMercy.Api.EventHandlers;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.Networking.Messaging.EventHandlers;
using NoMercy.Service.Extensions;

namespace NoMercy.Tests.Service.Extensions;

/// <summary>
/// <see cref="EventHandlerExtensions.AddSignalREventHandlers"/> is the registry
/// of every event handler that bridges the in-process event bus to SignalR (or,
/// for the inbox pipeline, to routing/classification). A handler that is
/// registered here but not resolved by <c>InitializeSignalREventHandlers</c>
/// never subscribes to anything and silently drops its events — exactly the
/// "ContentAnalysisHub declared but never mapped" bug class documented on
/// <see cref="ApplicationConfiguration"/>. These pin that EVERY handler this
/// method wires up is registered exactly once, as a Singleton (shared
/// subscription state, not per-request).
/// </summary>
[Trait("Category", "Unit")]
public class EventHandlerExtensionsTests
{
    private static readonly Type[] ExpectedSingletonHandlerTypes =
    [
        typeof(SignalRPlaybackEventHandler),
        typeof(SignalREncodingEventHandler),
        typeof(SignalRLibraryScanEventHandler),
        typeof(SignalRLibraryRefreshEventHandler),
        typeof(FileWatcherEventHandler),
        typeof(FolderPathEventHandler),
        typeof(MusicLikeEventHandler),
        typeof(SignalRNotificationEventHandler),
        typeof(DriveMonitorEventHandler),
        typeof(CastEventHandler),
        typeof(UserPermissionsEventHandler),
        typeof(InboxClassifier),
        typeof(InboxRoutingService),
        typeof(InboxClassifierEventHandler),
        typeof(SignalRInboxEventHandler),
    ];

    [Fact]
    public void AddSignalREventHandlers_RegistersEveryHandlerAsSingleton()
    {
        ServiceCollection services = new();

        services.AddSignalREventHandlers();

        foreach (Type handlerType in ExpectedSingletonHandlerTypes)
        {
            services
                .Should()
                .ContainSingle(
                    d => d.ServiceType == handlerType,
                    $"{handlerType.Name} must be registered exactly once"
                )
                .Which.Lifetime.Should()
                .Be(
                    ServiceLifetime.Singleton,
                    $"{handlerType.Name} holds shared subscription state"
                );
        }
    }

    [Fact]
    public void AddSignalREventHandlers_RegistersInboxMetadataProbeAndTagReader()
    {
        ServiceCollection services = new();

        services.AddSignalREventHandlers();

        services.Should().Contain(d => d.ServiceType == typeof(IInboxMetadataProbe));
        services.Should().Contain(d => d.ServiceType == typeof(IInboxAudioTagReader));
    }

    [Fact]
    public void AddSignalREventHandlers_ReturnsSameServiceCollectionForChaining()
    {
        ServiceCollection services = new();

        IServiceCollection result = services.AddSignalREventHandlers();

        result.Should().BeSameAs(services);
    }
}
