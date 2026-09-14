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

using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NoMercy.DiscFormat.Abstractions.Disc;
using NoMercy.DiscFormat.Composition;
using NoMercy.DiscFormat.Dvd.Identity;
using NoMercy.DiscFormat.LibBluray;
using NoMercy.DiscFormat.LibBluray.Identity;
using NoMercy.Encoder.Audio;
using NoMercy.OpticalMedia.Audio;
using NoMercy.OpticalMedia.Capabilities;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Drives.Backends;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.OpticalMedia.Sources.Bluray;
using NoMercy.Providers.MusicBrainz.Client;

namespace NoMercy.OpticalMedia.Composition;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNoMercyOpticalMedia(this IServiceCollection services)
    {
        services.TryAddSingleton<IDriveBackend>(sp =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    return new WindowsDriveBackend(
                        sp.GetRequiredService<ILogger<WindowsDriveBackend>>()
                    );
                }
                catch
                {
                    return new PollingDriveBackend(
                        sp.GetRequiredService<ILogger<PollingDriveBackend>>()
                    );
                }
            }
            return new PollingDriveBackend(sp.GetRequiredService<ILogger<PollingDriveBackend>>());
        });

        services.TryAddSingleton<IDriveMonitor, DriveMonitor>();
        services.TryAddSingleton<DriveLockRegistry>();
        services.TryAddSingleton<Onboarding.DiscOnboardingSessionStore>();
        services.TryAddSingleton<Onboarding.DiscOnboardingOrchestrator>();

        services.TryAddTransient<IDiscRipper, DiscRipper>();
        services.TryAddTransient<IAudioMetadataWriter, TagLibAudioMetadataWriter>();
        services.TryAddScoped<IDiscConfirmationService, DiscConfirmationService>();
        services.TryAddScoped<IDiscRipPreparationService, DiscRipPreparationService>();

        // IDiscSource implementations — registered as IEnumerable so the
        // factory can pick the right one per disc type. CdDiscSource
        // covers both audio and data CDs since OpticalDiscType.Cd doesn't
        // differentiate.
        services.AddTransient<IDiscSource, BlurayDiscSource>();
        services.AddTransient<IDiscSource, Sources.Dvd.DvdDiscSource>();
        services.AddTransient<IDiscSource, Sources.AudioCd.CdDiscSource>();
        services.TryAddSingleton<DiscSourceFactory>();

        // Identification layer — contract-based DI, one impl per disc type.
        // WindowsTocReader is registered first on Windows; TryAdd ensures
        // NullTocReader is only wired when no Windows reader is present.
        if (OperatingSystem.IsWindows())
            services.AddTransient<ITocReader, WindowsTocReader>();
        services.TryAddTransient<ITocReader, NullTocReader>();
        services.TryAddTransient<MusicBrainzDiscClient>();
        services.AddTransient<IDiscIdentifier, VideoDiscIdentifier>();
        services.AddTransient<IDiscIdentifier, AudioCdIdentifier>();
        services.TryAddTransient<DiscIdentificationService>();

        services.TryAddTransient<Live.ILiveDiscSession, Live.LiveDiscSession>();
        services.TryAddSingleton<Live.IDiscSessionRegistry, Live.DiscSessionRegistry>();

        services.TryAddSingleton<FfmpegBluRayCapability>();
        services.AddHostedService<BluRayCapabilityStartupService>();

        // Disc identity — contract-based DI, one reader per disc kind (same
        // dispatch shape as ITocReader/IDiscIdentifier above). The Blu-ray
        // reader needs a fresh ILibBluray per read (it Opens/Disposes one
        // native handle per call), so it takes a factory rather than a
        // shared instance.
        services.AddTransient<IDiscIdentityReader, DvdIdentityReader>();
        services.AddTransient<IDiscIdentityReader>(_ => new BlurayIdentityReader(() =>
            new LibBlurayClient()
        ));
        services.TryAddTransient<DiscIdentityDispatcher>();

        return services;
    }
}
