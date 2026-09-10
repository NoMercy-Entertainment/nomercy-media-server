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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoMercy.Encoder.Pipeline;
using NoMercy.Events;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Hooks;
using NoMercy.Plugins.Hub;
using NoMercy.Plugins.Verification;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Plugins;

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

        // Built from the container rather than by the parameterless constructor,
        // because one stage asks the repository where a plugin came from and
        // that answer is the only thing trust may rest on.
        services.AddSingleton<IPluginVerifier>(sp => new PluginVerifier([
            new AbiVerificationStage(),
            new ChecksumVerificationStage(),
            new TrustedRepositoryVerificationStage(() => sp.GetService<IPluginRepository>()),
            new SignatureVerificationStage(),
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

        services.AddSingleton<IPluginHubRouter, PluginHubRouter>();

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
            sp.GetRequiredService<IPluginHubContextFactory>()
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
                pluginId => sp.GetService<IPluginCronRegistrar>()?.RegisterPlugin(pluginId)
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

    // Pre-build registration: discovers IPluginServiceRegistrator implementations before
    // the DI container is built so plugins can contribute services to the host container.
    //
    // LIMITATION: This loads each plugin assembly into a temporary AssemblyLoadContext for
    // service-registration discovery only. The runtime load (LoadAllAsync) loads it again
    // into the canonical context. This two-phase approach is a known-fragile MVI shortcut —
    // see the future refactor TODO: introduce a proper plugin DI sub-container that avoids
    // loading the same assembly twice in different contexts.
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
            if (dirName is "configurations" or "data")
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

                // Load into a temporary context for discovery only; unloaded after registration.
                PluginLoadContext discoveryCtx = new(assemblyPath);
                try
                {
                    Assembly assembly = discoveryCtx.LoadFromAssemblyPath(assemblyPath);

                    IEnumerable<Type> registratorTypes = assembly
                        .GetTypes()
                        .Where(t =>
                            typeof(IPluginServiceRegistrator).IsAssignableFrom(t)
                            && t is { IsAbstract: false, IsInterface: false }
                        );

                    foreach (Type registratorType in registratorTypes)
                    {
                        if (
                            Activator.CreateInstance(registratorType)
                            is IPluginServiceRegistrator registrator
                        )
                        {
                            registrator.RegisterServices(services);
                        }
                    }

                    // Marked because the plugin was PRESENT for this pass, not
                    // because it registered anything in it.
                    //
                    // This is also where a plugin's controllers are picked up,
                    // and the advisor consults the same flag for routes. Gating
                    // it on `registeredAny` meant a plugin that declares `rest`
                    // and contributes no services — which is most of them — was
                    // never marked, so it reported "needs a restart" after every
                    // boot including the restart the owner had just performed.
                    RestartAdvisorIn(services)?.MarkRegisteredAtStartup(manifest.Id);
                }
                finally
                {
                    discoveryCtx.Unload();
                }
            }
            catch (Exception)
            {
                // Never throw during ConfigureServices — boot must continue without this plugin's services.
            }
        }

        return services;
    }
}
