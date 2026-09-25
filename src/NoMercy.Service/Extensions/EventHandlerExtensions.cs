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

using Microsoft.EntityFrameworkCore;
using NoMercy.Api.EventHandlers;
using NoMercy.Api.Middleware;
using NoMercy.Api.Services.Music;
using NoMercy.Authorization;
using NoMercy.Data.EventHandlers;
using NoMercy.Data.Notifications;
using NoMercy.Database;
using NoMercy.Events;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Shows;
using NoMercy.Networking.Messaging;
using NoMercy.Networking.Messaging.EventHandlers;
using NoMercy.NmSystem.Auth;
using NoMercy.Notifications.Push;
using NoMercy.Notifications.Transports;
using NoMercy.OpticalMedia.Onboarding;
using NoMercy.Storage;

namespace NoMercy.Service.Extensions;

public static class EventHandlerExtensions
{
    public static IServiceCollection AddSignalREventHandlers(this IServiceCollection services)
    {
        services.AddSingleton<SignalRPlaybackEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(
                sp.GetRequiredService<ILogger<SignalRPlaybackEventHandler>>(),
                eventBus,
                clientMessenger
            );
        });

        services.AddSingleton<SignalREncodingEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(
                sp.GetRequiredService<ILogger<SignalREncodingEventHandler>>(),
                eventBus,
                clientMessenger
            );
        });

        services.AddSingleton<ActivityEventHandler>(sp =>
            new(
                sp.GetRequiredService<ILogger<ActivityEventHandler>>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<NoMercy.Database.Activity.IActivityLogger>()
            )
        );

        services.AddSingleton<SignalRLibraryScanEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(
                sp.GetRequiredService<ILogger<SignalRLibraryScanEventHandler>>(),
                eventBus,
                clientMessenger
            );
        });

        services.AddSingleton<SignalRLibraryRefreshEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(eventBus, clientMessenger);
        });

        services.AddSingleton<FileWatcherEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IStorageDriver storageDriver = sp.GetRequiredService<IStorageDriver>();
            IStorageFactory storageFactory = sp.GetRequiredService<IStorageFactory>();
            return new(
                sp.GetRequiredService<ILogger<FileWatcherEventHandler>>(),
                eventBus,
                storageDriver,
                storageFactory
            );
        });

        services.AddSingleton<FolderPathEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IServiceScopeFactory scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            return new(
                eventBus,
                scopeFactory,
                sp.GetRequiredService<IUserCache>(),
                sp.GetRequiredService<IServedFolderRegistry>()
            );
        });

        services.AddSingleton<MusicLikeEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            MusicPlaybackService playbackService = sp.GetRequiredService<MusicPlaybackService>();
            return new(eventBus, playbackService);
        });

        services.AddSingleton<SignalRNotificationEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(eventBus, clientMessenger);
        });

        services.AddSingleton<IWebPushEnvelope, WebPushEnvelope>();
        services.AddSingleton<PushKeyClient>();
        services.AddSingleton<IPushKeyClient>(sp => new CachingPushKeyClient(
            sp.GetRequiredService<PushKeyClient>()
        ));
        services.AddSingleton<IPushRelayClient, PushRelayClient>();
        services.AddSingleton<IPushDispatcher, PushDispatcher>();
        services.AddSingleton<IPushDispatchQueue, PushDispatchQueue>();
        services.AddSingleton<INotificationTransport, SignalRNotificationTransport>();
        services.AddSingleton<INotificationTransport, PushNotificationTransport>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<NotificationSink>();
        services.AddHostedService<PushDispatchWorker>();

        services.AddSingleton<IPlayableMediaProbe, PlayableMediaProbe>();

        services.AddSingleton<PushNotificationEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IAuthTokenStore authTokenStore = sp.GetRequiredService<IAuthTokenStore>();
            NotificationSink notificationSink = sp.GetRequiredService<NotificationSink>();
            IPlayableMediaProbe playableMediaProbe = sp.GetRequiredService<IPlayableMediaProbe>();
            return new(eventBus, authTokenStore, notificationSink, playableMediaProbe);
        });

        services.AddSingleton<DriveMonitorEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(eventBus, clientMessenger);
        });

        services.AddSingleton<DiscOnboardingEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(eventBus, clientMessenger);
        });

        services.AddSingleton<DiscOnboardingCompletionEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            DiscOnboardingSessionStore store = sp.GetRequiredService<DiscOnboardingSessionStore>();
            IDbContextFactory<MediaContext> contextFactory = sp.GetRequiredService<
                IDbContextFactory<MediaContext>
            >();
            return new(eventBus, store, contextFactory);
        });

        services.AddSingleton<CastEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(eventBus, clientMessenger);
        });

        services.AddSingleton<UserPermissionsEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(
                sp.GetRequiredService<ILogger<UserPermissionsEventHandler>>(),
                eventBus,
                clientMessenger
            );
        });

        services.AddSingleton<PluginConsentEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IUserCache userCache = sp.GetRequiredService<IUserCache>();
            return new(eventBus, userCache);
        });

        services.AddSingleton<IInboxMetadataProbe, TmdbMusicBrainzMetadataProbe>();
        services.AddSingleton<IInboxAudioTagReader>(sp =>
        {
            IStorageFactory storageFactory = sp.GetRequiredService<IStorageFactory>();
            return new StorageAudioTagReader(storageFactory);
        });
        services.AddSingleton<InboxClassifier>(sp =>
        {
            IInboxMetadataProbe probe = sp.GetRequiredService<IInboxMetadataProbe>();
            IInboxAudioTagReader tagReader = sp.GetRequiredService<IInboxAudioTagReader>();
            IMediaTypeClassifier mediaTypeClassifier =
                sp.GetRequiredService<IMediaTypeClassifier>();
            return new(probe, tagReader, mediaTypeClassifier);
        });
        services.AddSingleton<InboxRoutingService>(sp =>
        {
            IStorageFactory storageFactory = sp.GetRequiredService<IStorageFactory>();
            return new(storageFactory, new());
        });
        services.AddSingleton<InboxClassifierEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            InboxClassifier classifier = sp.GetRequiredService<InboxClassifier>();
            InboxRoutingService routing = sp.GetRequiredService<InboxRoutingService>();
            IStorageFactory storageFactory = sp.GetRequiredService<IStorageFactory>();
            return new(
                sp.GetRequiredService<ILogger<InboxClassifierEventHandler>>(),
                eventBus,
                classifier,
                routing,
                () => sp.GetRequiredService<IDbContextFactory<MediaContext>>().CreateDbContext(),
                storageFactory
            );
        });
        services.AddSingleton<SignalRInboxEventHandler>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            IClientMessenger clientMessenger = sp.GetRequiredService<IClientMessenger>();
            return new(
                sp.GetRequiredService<ILogger<SignalRInboxEventHandler>>(),
                eventBus,
                clientMessenger
            );
        });

        return services;
    }

    public static IServiceProvider InitializeSignalREventHandlers(
        this IServiceProvider serviceProvider
    )
    {
        // Resolve handlers to trigger their construction and event subscriptions
        serviceProvider.GetRequiredService<SignalRPlaybackEventHandler>();
        serviceProvider.GetRequiredService<SignalREncodingEventHandler>();
        serviceProvider.GetRequiredService<ActivityEventHandler>();
        serviceProvider.GetRequiredService<SignalRLibraryScanEventHandler>();
        serviceProvider.GetRequiredService<SignalRLibraryRefreshEventHandler>();
        serviceProvider.GetRequiredService<FileWatcherEventHandler>();
        serviceProvider.GetRequiredService<FolderPathEventHandler>();
        serviceProvider.GetRequiredService<MusicLikeEventHandler>();
        serviceProvider.GetRequiredService<SignalRNotificationEventHandler>();
        serviceProvider.GetRequiredService<PushNotificationEventHandler>();
        serviceProvider.GetRequiredService<DriveMonitorEventHandler>();
        serviceProvider.GetRequiredService<DiscOnboardingEventHandler>();
        serviceProvider.GetRequiredService<DiscOnboardingCompletionEventHandler>();
        serviceProvider.GetRequiredService<CastEventHandler>();
        serviceProvider.GetRequiredService<UserPermissionsEventHandler>();
        serviceProvider.GetRequiredService<PluginConsentEventHandler>();
        serviceProvider.GetRequiredService<InboxClassifierEventHandler>();
        serviceProvider.GetRequiredService<SignalRInboxEventHandler>();

        return serviceProvider;
    }
}
