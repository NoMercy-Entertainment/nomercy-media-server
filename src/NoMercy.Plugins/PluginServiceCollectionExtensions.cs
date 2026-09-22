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
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoMercy.Encoder.Pipeline;
using NoMercy.Events;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Dependencies;
using NoMercy.PluginSdk.Entitlements;
using NoMercy.PluginSdk.Guests;
using NoMercy.PluginSdk.Hooks;
using NoMercy.PluginSdk.Hub;
using NoMercy.PluginSdk.Lan;
using NoMercy.PluginSdk.Library;
using NoMercy.PluginSdk.Media;
using NoMercy.PluginSdk.Network;
using NoMercy.PluginSdk.Offline;
using NoMercy.PluginSdk.Quotas;
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Runtime;
using NoMercy.PluginSdk.Search;
using NoMercy.PluginSdk.Sideload;
using NoMercy.PluginSdk.Storage;
using NoMercy.PluginSdk.Telemetry;
using NoMercy.PluginSdk.UserData;
using NoMercy.PluginSdk.Verification;
using NoMercy.PluginSdk.Watchdog;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.PluginSdk;

public static class PluginServiceCollectionExtensions
{
    public static IServiceCollection AddPluginSystem(
        this IServiceCollection services,
        string pluginsPath
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsPath);

        // The platform stores plugin secrets, so it needs data protection and
        // says so rather than assuming the host got there first. The call is
        // additive: where the host also configures it — persisting the key ring
        // to disk — that configuration still applies, and a host that forgets
        // gets a working platform instead of a resolve failure at plugin load.
        services.AddDataProtection();

        // One clock for the whole platform. Seven services need one, and seven
        // fallbacks to the system clock would be seven places a test that
        // moves time could quietly fail to.
        services.TryAddSingleton(TimeProvider.System);

        // The host's own registration list, kept so a plugin's container can
        // hand those service types straight back to the host rather than
        // building second copies of them. Read at plugin load, by which time
        // the collection is complete.
        services.TryAddSingleton(new PluginHostServiceCollection(services));

        // What a plugin holds outside the process, so stopping it gives all of
        // it back. Shared by sockets, router mappings and child processes.
        services.TryAddSingleton<IPluginResourceLedger, PluginResourceLedger>();

        // The two halves of being findable on the owner's own network. Both
        // are TryAdd so a host that speaks to its network some other way can
        // replace either without replacing the platform.
        // The two ways a plugin leaves everything the host mediates. Both are
        // registered so the facades exist; both refuse on their own terms.
        services.TryAddSingleton<IPluginProcessStarter, SystemProcessStarter>();
        services.TryAddSingleton<INativeLibraryLoader, SystemNativeLibraryLoader>();
        services.TryAddSingleton<IPluginBundleSignature, NothingIsSigned>();

        services.TryAddSingleton<IPluginServiceDiscoveryClient, MulticastDiscoveryClient>();
        services.TryAddSingleton<IPluginPortMapClient, NatPortMapClient>();
        services.TryAddSingleton<PluginPortMapRenewalService>();
        services.AddHostedService(sp => sp.GetRequiredService<PluginPortMapRenewalService>());

        // Built from the container rather than by the parameterless constructor,
        // because one stage asks the repository where a plugin came from and
        // that answer is the only thing trust may rest on.
        // Keyed by the id a signature block names so a publisher can rotate
        // without a flag day. None configured is not an error: the stage reads
        // that emptiness and records the question as unanswered rather than
        // refusing every marketplace install on a server that trusts nobody.
        services.AddSingleton<IPluginTrustedKeys>(sp => new PluginTrustedKeys(
            sp.GetService<IConfiguration>()
                ?.GetSection("Plugins:TrustedKeys")
                .Get<Dictionary<string, string>>()
                ?? []
        ));

        // The two answers a server needs about a plugin before it runs: is
        // this build still allowed, and may this owner run it. Both are kept
        // on disk so a server that starts with no connection still knows what
        // it was last told.
        services.AddSingleton<IPluginRevocationStore>(sp => new PluginRevocationStore(
            events: sp.GetService<IEventBus>()
        ));
        services.AddSingleton<IPluginEntitlementStore>(sp => new PluginEntitlementStore(
            events: sp.GetService<IEventBus>()
        ));
        // One answer for every screen that can show or open a plugin. The
        // membership side is registered by the host, which has the user list;
        // with none registered nothing is shared and the owner still sees
        // everything they installed.
        services.AddSingleton<IPluginGuestInstallStore>(new PluginGuestInstallStore());
        services.AddSingleton<PluginGuestInstaller>(sp =>
        {
            IStorageDriver driver = sp.GetRequiredService<IStorageDriver>();
            IStorage storage = new LocalStorage(driver, new([pluginsPath], driver));
            IPluginConfiguration configuration = new PluginConfiguration(
                Path.Combine(pluginsPath, "data", "platform"),
                storage
            );

            // Built here the way the manager builds its own: the purge is not
            // a registered service, and a guest leaving has to remove exactly
            // what uninstalling would.
            return new(
                sp.GetRequiredService<IPluginGuestInstallStore>(),
                new PluginDataPurge(
                    pluginsPath,
                    storage,
                    sp.GetRequiredService<IPluginConsentService>(),
                    new ConfigPluginGrantStore(configuration),
                    configuration
                ),
                sp.GetService<IEventBus>()
            );
        });
        services.AddSingleton<IPluginInstallFacts>(sp => new PluginInstallFacts(
            sp.GetRequiredService<IPluginManager>(),
            sp.GetRequiredService<IPluginGuestInstallStore>()
        ));
        services.AddSingleton<IPluginAccessResolver>(sp => new PluginAccessResolver(
            sp.GetRequiredService<IPluginInstallFacts>(),
            sp.GetRequiredService<IPluginEntitlementStore>(),
            sp.GetService<IPluginMembership>() ?? new NobodyIsAMember(),
            sp.GetRequiredService<TimeProvider>(),
            () => sp.GetService<IPluginOwner>()?.Id ?? Guid.Empty
        ));

        // The search box asks every plugin the caller may use, at once and with
        // a deadline: one slow provider was one slow search box for everyone.
        services.AddSingleton<IPluginSearchService>(sp => new PluginSearchService(
            sp.GetRequiredService<IPluginManager>(),
            sp.GetRequiredService<IPluginAccessResolver>(),
            sp.GetRequiredService<ILogger<PluginSearchService>>()
        ));

        // Every device a person is signed in on is told their own answer when
        // one of the four gates moves. A client that missed a message would
        // otherwise stay wrong until somebody reloaded it.
        services.AddSingleton<PluginAccessNotifier>(sp =>
            new(
                sp.GetRequiredService<IPluginAccessResolver>(),
                sp.GetService<IPluginMembership>() ?? new NobodyIsAMember(),
                sp.GetRequiredService<IPluginManifestSource>(),
                sp.GetService<IPluginAccessHub>() ?? new NobodyIsListening(),
                () => sp.GetService<IPluginOwner>()?.Id ?? Guid.Empty
            )
        );
        services.AddSingleton<PluginAccessChangedListener>(sp =>
            new(sp.GetRequiredService<IEventBus>(), sp.GetRequiredService<PluginAccessNotifier>())
        );

        // The key every media ticket is signed with, derived once from this
        // server's data-protection material. It is not read from configuration
        // a plugin can see, and it changes on restart, which expires every
        // outstanding ticket. Tickets live for minutes, so that costs a viewer
        // one reopen and removes a key that would otherwise sit on disk.
        // Resolved lazily through the manager: the broker and the media
        // factory both ask what a plugin declared, and neither should be able
        // to install or uninstall one to find out.
        services.TryAddSingleton<IPluginManifestSource>(sp => new PluginManagerManifestSource(
            sp.GetRequiredService<IPluginManager>()
        ));

        // The three questions asked before a plugin acts, and the tally of what
        // was refused. Registered here rather than built by each caller so one
        // plugin's refusals are counted once.
        services.TryAddSingleton<IPluginRefusalCounter>(new PluginRefusalCounter());
        services.TryAddSingleton<IPluginCapabilityBroker>(sp => new PluginCapabilityBroker(
            sp.GetRequiredService<IPluginManifestSource>(),
            sp.GetRequiredService<IPluginConsentService>(),
            sp.GetRequiredService<IPluginGrantStore>(),
            sp.GetRequiredService<IPluginRefusalCounter>(),
            sp.GetRequiredService<ILogger<PluginCapabilityBroker>>()
        ));

        // Nobody is asking outside a request, and a host that never registers
        // one says so rather than minting a link bound to the empty account.
        services.TryAddSingleton<IPluginCallerAccessor>(NoPluginCaller.Instance);

        // The ceilings and what happens when a plugin goes past them. The
        // sampler measures nothing in this stage and says so by answering
        // null, which the watchdog reads as a plugin to leave alone; a number
        // invented here would produce restarts nobody could explain.
        services.TryAddSingleton<IPluginResourceSampler>(new PluginAssemblyResourceSampler());
        // One store answers both: the ceilings the watchdog reads and the
        // allowances the meter reads are the same numbers, and two stores
        // would be two places for an owner's change to land in one of.
        PluginQuotaStore quotas = new();
        services.TryAddSingleton<IPluginQuotaSource>(quotas);
        services.TryAddSingleton(quotas);
        services.TryAddSingleton<IPluginResourceCeilingSource>(quotas);
        // A plugin runs only while what it leans on runs, and a free
        // dependency installs beside it rather than leaving the owner to work
        // out why nothing started.
        services.AddSingleton<PluginDependencyGate>(sp =>
            new(
                sp.GetRequiredService<IPluginManifestSource>(),
                sp.GetRequiredService<IPluginEntitlementStore>(),
                sp.GetRequiredService<TimeProvider>(),
                () => sp.GetService<IPluginOwner>()?.Id ?? Guid.Empty
            )
        );
        services.AddSingleton<IPluginCatalogue>(sp => new PluginRepositoryCatalogue(
            sp.GetRequiredService<IPluginRepository>()
        ));
        services.AddSingleton<PluginDependencyResolver>(sp =>
            new(
                sp.GetRequiredService<IPluginCatalogue>(),
                sp.GetRequiredService<IPluginManifestSource>()
            )
        );

        // The four questions asked before a plugin runs. Registration fixes
        // the order once, here, so no caller can ask them in an order that
        // tells an owner to buy a build that was withdrawn.
        services.AddSingleton<PluginRevocationRunCheck>(sp =>
            new(
                new PluginRevocationGate(
                    sp.GetRequiredService<IPluginRevocationStore>(),
                    sp.GetRequiredService<TimeProvider>()
                ),
                sp.GetRequiredService<IPluginRevocationStore>()
            )
        );
        services.AddSingleton<PluginEntitlementRunCheck>(sp =>
            new(
                new PluginEntitlementGate(
                    sp.GetRequiredService<IPluginEntitlementStore>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetService<IPluginOwner>()?.Id ?? Guid.Empty
                )
            )
        );
        services.AddSingleton<PluginDependencyRunCheck>(sp =>
            new(sp.GetRequiredService<PluginDependencyGate>())
        );
        services.AddSingleton<PluginConsentRunCheck>(sp =>
            new(sp.GetRequiredService<IPluginConsentService>())
        );
        services.AddSingleton<IPluginRunGate>(sp => new PluginRunGate(
            [
                sp.GetRequiredService<PluginRevocationRunCheck>(),
                sp.GetRequiredService<PluginEntitlementRunCheck>(),
                sp.GetRequiredService<PluginDependencyRunCheck>(),
                sp.GetRequiredService<PluginConsentRunCheck>(),
            ],
            sp.GetRequiredService<IPluginManifestSource>()
        ));

        // The granted folders, cached, because a plugin reads them in a loop.
        // The free-space probe is registered by the host, which has the
        // database; with none, a plugin gets no server facade rather than a
        // measurement nothing took.
        services.TryAddSingleton<IPluginGrantedLocations>(sp => new PluginGrantedLocations(
            sp.GetService<IPluginFolderCatalog>(),
            sp.GetRequiredService<IPluginGrantStore>()
        ));

        // One folder per person per plugin, which is what makes handing
        // somebody their data and removing it possible at all.
        services.AddSingleton<PluginUserDataExporter>(
            new PluginUserDataExporter(Path.Combine(pluginsPath, "data"))
        );

        // Software on the owner's network that cannot sign in. Kept on disk,
        // because the television does not come back and ask for a new address
        // after the server restarts.
        services.TryAddSingleton<IPluginLanDeviceStore>(new PluginLanDeviceStore());
        services.AddSingleton<PluginLanDeviceMinter>(sp =>
            new(
                sp.GetRequiredService<IPluginLanDeviceStore>(),
                sp.GetRequiredService<TimeProvider>()
            )
        );

        services.AddSingleton<PluginQuotaMeter>(sp =>
            new(
                sp.GetRequiredService<IPluginQuotaSource>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IPluginRefusalCounter>()
            )
        );
        services.TryAddSingleton<IPluginWatchdogLifecycle>(sp => new PluginManagerWatchdogLifecycle(
            // Lazily, for the same reason the media factory is: the manager is
            // what the watchdog acts on, and it is built after this.
            () => sp.GetService<IPluginManager>(),
            sp.GetRequiredService<ILogger<PluginManagerWatchdogLifecycle>>()
        ));
        services.AddSingleton<PluginWatchdog>(sp =>
            new(
                sp.GetRequiredService<IPluginResourceCeilingSource>(),
                sp.GetRequiredService<IPluginWatchdogLifecycle>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IPluginCrashCounter>()
            )
        );
        services.AddHostedService<PluginWatchdogService>();

        // What this server tells NoMercy about its plugins. Refusal counts
        // always; crash and ceiling counters only if the owner said yes; a
        // sideload never, not even that it exists.
        services.TryAddSingleton<IPluginCrashCounter, PluginCrashCounter>();
        services.TryAddSingleton<IPluginTelemetrySink>(sp => new PluginHttpTelemetrySink(
            TelemetryClient(),
            sp.GetRequiredService<IAuthTokenStore>(),
            sp.GetRequiredService<ILogger<PluginHttpTelemetrySink>>()
        ));
        services.AddSingleton<PluginCrashListener>(sp =>
            new(sp.GetRequiredService<IEventBus>(), sp.GetRequiredService<IPluginCrashCounter>())
        );
        services.AddSingleton<PluginTelemetryReporter>(sp =>
            new(
                sp.GetRequiredService<IPluginManifestSource>(),
                sp.GetRequiredService<IPluginRefusalCounter>(),
                sp.GetRequiredService<IPluginCrashCounter>(),
                sp.GetRequiredService<IPluginInstallFacts>(),
                sp.GetRequiredService<IPluginTelemetrySink>(),
                // Read every window rather than once at startup, so an owner
                // changing their mind takes effect without a restart.
                () => PluginTelemetryOptions.Load(),
                sp.GetRequiredService<TimeProvider>(),
                Info.DeviceId
            )
        );
        services.AddHostedService<PluginTelemetryService>();
        services.AddHostedService<PluginListenerService>();

        // In memory: a channel carries a callback that resolves a
        // credential-bearing address, and a callback cannot be written to disk.
        // A plugin republishes on start, which it already does to pick up what
        // the provider changed.
        services.TryAddSingleton<IPluginLiveStore>(new PluginLiveStore());

        services.AddSingleton<IPluginMediaFactory>(sp => new PluginMediaFactory(
            sp.GetRequiredService<IPluginManifestSource>(),
            sp.GetRequiredService<IPluginCapabilityBroker>(),
            sp.GetRequiredService<IPluginGrantStore>(),
            sp.GetRequiredService<PluginMediaTicketMinter>(),
            sp.GetRequiredService<IPluginCallerAccessor>(),
            sp.GetRequiredService<IPluginLiveStore>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<PluginQuotaMeter>()
        ));

        services.AddSingleton<PluginMediaTicketMinter>(sp =>
            new(
                sp.GetRequiredService<TimeProvider>(),
                SHA256.HashData(
                    sp.GetRequiredService<IDataProtectionProvider>()
                        .CreateProtector("NoMercy.Plugins.Media.Tickets")
                        .Protect("media-ticket-signing-key"u8.ToArray())
                )
            )
        );

        // A file the owner dropped in themselves. Developer mode is read per
        // call, so turning it off takes effect on the next install rather than
        // the next restart.
        services.TryAddSingleton<IPluginDeveloperModeSource>(new PluginDeveloperModeFile());

        services.AddSingleton<PluginSideloadPolicy>(sp =>
            new(
                () => sp.GetRequiredService<IPluginDeveloperModeSource>().Enabled,
                sp.GetRequiredService<IPluginEntitlementStore>(),
                sp.GetRequiredService<TimeProvider>(),
                () => sp.GetService<IPluginOwner>()?.Id ?? Guid.Empty
            )
        );

        services.AddSingleton<PluginOfflineBundleImporter>(sp =>
            new(
                sp.GetRequiredService<IPluginEntitlementStore>(),
                sp.GetRequiredService<IPluginRevocationStore>(),
                sp.GetRequiredService<IPluginTrustedKeys>(),
                sp.GetRequiredService<TimeProvider>()
            )
        );

        services.AddSingleton<IPluginVerifier>(sp => new PluginVerifier([
            new AbiVerificationStage(),
            new ChecksumVerificationStage(),
            new TrustedRepositoryVerificationStage(() => sp.GetService<IPluginRepository>()),
            new SignatureVerificationStage(sp.GetRequiredService<IPluginTrustedKeys>()),
        ]));
        PluginAssemblyTracker assemblyTracker = new();
        services.AddSingleton<IPluginAssemblyTracker>(assemblyTracker);

        // An instance, not a type: RegisterPluginServicesFromManifests runs
        // before the provider is built and has to record into the same object
        // the running server later reads.
        services.AddSingleton<IPluginRestartAdvisor>(new PluginRestartAdvisor(assemblyTracker));

        // Bound so a deployment can add a shared framework package without a
        // code change, which is what the type always said it was for.
        //
        // Read through the provider rather than BindConfiguration: a host with
        // no IConfiguration registered — a test, an embedded use — must still
        // get a working platform rather than a resolve failure at plugin load.
        services
            .AddOptions<PluginHostOptions>()
            .Configure<IServiceProvider>(
                (options, sp) =>
                    sp.GetService<IConfiguration>()?.GetSection("Plugins:Host").Bind(options)
            );

        // The catalogue side of the platform. Built here rather than by the
        // async factory: the container resolves synchronously, and startup
        // calls LoadAsync once the host is up so no resolve waits on disk.
        services.AddSingleton<IPluginRepository>(sp =>
        {
            IStorageDriver driver = sp.GetRequiredService<IStorageDriver>();

            return new PluginRepository(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                sp.GetRequiredService<ILogger<PluginRepository>>(),
                pluginsPath,
                new LocalStorage(driver, new([pluginsPath], driver))
            );
        });

        services.AddSingleton<IPluginConsentStore>(sp =>
        {
            IStorageDriver driver = sp.GetRequiredService<IStorageDriver>();
            IStorage storage = new LocalStorage(driver, new([pluginsPath], driver));
            string platformDataFolder = Path.Combine(pluginsPath, "data", "platform");
            IPluginConfiguration configuration = new PluginConfiguration(
                platformDataFolder,
                storage
            );
            return new ConfigPluginConsentStore(configuration);
        });

        services.AddSingleton<IPluginConsentService, PluginConsentService>();

        // Grants live beside consent, in the same platform-scoped store and
        // never in a plugin's own folder: a plugin must not be able to edit the
        // record of what it was allowed to do.
        services.AddSingleton<IPluginGrantStore>(sp => new ConfigPluginGrantStore(
            PlatformConfiguration(sp, pluginsPath)
        ));

        // The library contracts default to the null objects declared in this
        // project. The host replaces them with the real ones — see
        // AddPluginLibraryAccess, called from the composition root, which is the
        // only place that may reference the database.
        services.TryAddSingleton<IPluginLibraryQuery, NullPluginLibraryQuery>();
        services.TryAddSingleton<IPluginLibraryWriterFactory, NullPluginLibraryWriterFactory>();

        // Lazily, like the cron registrar: the web host's hub context factory
        // takes this router, the plugin context factory takes that, and the
        // manager is built with the context factory. Handing the router the
        // manager itself here re-enters the manager's own factory forever.
        services.AddSingleton<IPluginHubRouter>(sp => new PluginHubRouter(
            () => sp.GetRequiredService<IPluginManager>(),
            sp.GetRequiredService<ILogger<PluginHubRouter>>()
        ));

        // The real one needs IHubContext<PluginHub>, which only exists where the
        // hub is mapped. TryAdd so the web host's registration wins and every
        // other host still gets a plugin platform that loads.
        services.TryAddSingleton<IPluginHubContextFactory, NullPluginHubContextFactory>();

        services.AddSingleton<IPluginContextFactory>(sp => new PluginContextFactory(
            sp.GetRequiredService<IEventBus>(),
            sp,
            PluginStorage(sp, pluginsPath),
            sp.GetRequiredService<IPluginGrantStore>(),
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<IPluginLibraryQuery>(),
            sp.GetRequiredService<IPluginLibraryWriterFactory>(),
            PlatformConfiguration(sp, pluginsPath),
            sp.GetRequiredService<IPluginHubContextFactory>(),
            // Optional: GetService, not GetRequiredService. A host that never
            // called AddPluginLibraryAccess (or the encoder/storage wiring)
            // still gets a working platform, the same TryAdd philosophy as the
            // library query and writer factory above — a plugin just finds the
            // corresponding hook gated to null instead of the resolve itself
            // failing for every plugin on every host.
            encoder: sp.GetService<IPluginEncoder>(),
            jobs: sp.GetService<IPluginJobs>(),
            musicQuery: sp.GetService<IPluginMusicQuery>(),
            audioToolsFactory: sp.GetService<IPluginAudioToolsFactory>(),
            derivedAudio: sp.GetService<IPluginDerivedAudio>(),
            analysisWriterFactory: sp.GetService<IPluginMusicAnalysisWriterFactory>(),
            // Lazily, like the cron registrar below: the media factory asks
            // what a plugin declared, that answer comes from the manager, and
            // the manager is built with this context factory. Resolving it
            // when a plugin context is actually made breaks the ring.
            mediaFactory: () => sp.GetService<IPluginMediaFactory>(),
            // Optional like the rest: a host that never wired media processing
            // gives a plugin a LibraryImport that refuses by name rather than
            // a resolve that fails for every plugin on every host.
            libraryScanner: sp.GetService<IPluginLibraryScanner>(),
            // The plugin's own folders and the facts about this server.
            // Optional like the rest: a host that wired no folder catalogue
            // gives a plugin a Storage that refuses by name.
            pluginsRoot: pluginsPath,
            folderCatalog: sp.GetService<IPluginFolderCatalog>(),
            grantedLocations: sp.GetService<IPluginGrantedLocations>(),
            freeSpace: sp.GetService<IPluginFreeSpaceProbe>(),
            quotas: sp.GetRequiredService<PluginQuotaMeter>(),
            serverVersion: Assembly.GetEntryAssembly()?.GetName().Version
        ));

        services.AddSingleton<IPluginManager>(sp =>
        {
            IEventBus eventBus = sp.GetRequiredService<IEventBus>();
            ILogger<PluginManager> logger = sp.GetRequiredService<ILogger<PluginManager>>();
            IStorageDriver driver = sp.GetRequiredService<IStorageDriver>();
            IPluginVerifier verifier = sp.GetRequiredService<IPluginVerifier>();
            IPluginConsentService consentService = sp.GetRequiredService<IPluginConsentService>();
            IStorage storage = new LocalStorage(driver, new([pluginsPath], driver));
            return new PluginManager(
                eventBus,
                sp,
                logger,
                pluginsPath,
                storage,
                driver,
                verifier,
                consentService,
                sp.GetRequiredService<IPluginContextFactory>(),
                sp.GetRequiredService<IOptions<PluginHostOptions>>().Value,
                sp.GetRequiredService<IPluginAssemblyTracker>(),
                // Resolved lazily: the cron registrar depends on the manager,
                // so taking it as a constructor argument here would be a cycle.
                pluginId => sp.GetService<IPluginCronRegistrar>()?.UnregisterPlugin(pluginId),
                // The counterpart, for the same reason: install, restart and
                // update all bring a scheduled-task plugin's instance back
                // without going through the boot path that registers it.
                pluginId => sp.GetService<IPluginCronRegistrar>()?.RegisterPlugin(pluginId),
                sp.GetRequiredService<PluginSideloadPolicy>(),
                sp.GetRequiredService<PluginGuestInstaller>()
            );
        });

        // Wire encoder plugins' GetProfile into the encoder's profile-override seam.
        // First plugin returning a non-null profile for the source wins.
        services.AddSingleton<IProfileOverride, PluginProfileOverride>();

        services.AddSingleton<IPluginCronRegistrar, PluginCronRegistrar>();

        // What plugins contribute to a library scan, merged in before the names
        // are resolved so their files go through the scanner's own parser.
        services.AddSingleton<IPluginMediaSourceProvider, PluginMediaSourceProvider>();

        // What plugins can fill in that the native provider left empty. Native
        // runs first; a plugin never overwrites what TMDB already answered.
        services.AddSingleton<IPluginMetadataResolver, PluginMetadataResolver>();

        // Additive auth claims: OnTokenValidated (ServiceConfiguration.Auth.cs) resolves
        // this per authenticated request to enrich the principal. It never decides auth.
        services.AddSingleton<IPluginClaimsAugmentor, PluginClaimsAugmentor>();

        return services;
    }

    /// <summary>
    /// The advisor instance already put in the collection by
    /// <see cref="AddPluginSystem"/>, read back before the provider exists.
    /// Null when plugin services were registered without it, which is not an
    /// error — the advisor simply has nothing to record.
    /// </summary>
    private static IPluginRestartAdvisor? RestartAdvisorIn(IServiceCollection services) =>
        services
            .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IPluginRestartAdvisor))
            ?.ImplementationInstance as IPluginRestartAdvisor;

    /// <summary>
    /// Its own client, with a short timeout. Telemetry is the least important
    /// thing this server does, and it must never be the thing holding a
    /// connection open while somebody is trying to watch something.
    /// </summary>
    private static HttpClient TelemetryClient() =>
        new()
        {
            BaseAddress = new(ExternalServicesConfig.Current.ApiServerBaseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };

    private static IStorage PluginStorage(IServiceProvider sp, string pluginsPath)
    {
        IStorageDriver driver = sp.GetRequiredService<IStorageDriver>();
        return new LocalStorage(driver, new([pluginsPath], driver));
    }

    private static IPluginConfiguration PlatformConfiguration(
        IServiceProvider sp,
        string pluginsPath
    ) =>
        new PluginConfiguration(
            Path.Combine(pluginsPath, "data", "platform"),
            PluginStorage(sp, pluginsPath)
        );

    public static void RegisterPluginServices(
        this IServiceCollection services,
        PluginManager pluginManager
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pluginManager);

        foreach (IPluginServiceRegistrator registrator in pluginManager.GetServiceRegistrators())
        {
            registrator.RegisterServices(services);
        }
    }

    /// <summary>
    /// Records which plugins were present before the request pipeline was
    /// built, which is the only moment a plugin's routes can join it.
    /// <para>
    /// It used to load every plugin assembly a second time, in a throwaway load
    /// context, to register that plugin's services into the host's container.
    /// A plugin now gets its own container at load (see
    /// <c>PluginInstanceFactory.ChildContainer</c>), so nothing here loads
    /// anything: the manifest is read, and that is all.
    /// </para>
    /// </summary>
    public static IServiceCollection RegisterPluginServicesFromManifests(
        this IServiceCollection services,
        string pluginsPath
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!Directory.Exists(pluginsPath))
            return services;

        foreach (string pluginDir in Directory.EnumerateDirectories(pluginsPath))
        {
            string dirName = Path.GetFileName(pluginDir);
            if (dirName is "configurations" or "data" || dirName.StartsWith('.'))
                continue;

            string manifestPath = Path.Combine(pluginDir, "plugin.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                string manifestJson = File.ReadAllText(manifestPath);
                PluginManifest manifest = PluginManifestParser.Parse(manifestJson);
                string assemblyPath = Path.Combine(pluginDir, manifest.Assembly);

                if (!File.Exists(assemblyPath))
                    continue;

                // Marked because the plugin was PRESENT for this pass, not
                // because it registered anything in it.
                //
                // This is also where a plugin's controllers are picked up, and
                // the advisor consults the same flag for routes. Gating it on
                // what a plugin registered meant a plugin that declares `rest`
                // and contributes no services — which is most of them — was
                // never marked, so it reported "needs a restart" after every
                // boot including the restart the owner had just performed.
                RestartAdvisorIn(services)?.MarkRegisteredAtStartup(manifest.Id.Value);
            }
            catch (Exception)
            {
                // Never throw during ConfigureServices — boot must continue without this plugin's services.
            }
        }

        return services;
    }
}

/// <summary>
/// What a host that never registered a membership source answers: nobody is a
/// member. The owner still sees everything they installed, and nothing is
/// shared with people the platform cannot confirm belong here.
/// </summary>
/// <summary>
/// What a host with no hub does with an access answer: nothing. Outside the
/// web host there is nobody connected to tell.
/// </summary>
internal sealed class NobodyIsListening : IPluginAccessHub
{
    public void Send(Guid userId, Ulid pluginId, string access) { }
}

internal sealed class NobodyIsAMember : IPluginMembership
{
    public bool IsAcceptedMember(Guid userId) => false;

    public IReadOnlyList<Guid> EveryoneOn(Guid ownerId) => ownerId == Guid.Empty ? [] : [ownerId];

    public int SeatsTakenFor(Ulid pluginId) => 0;
}
