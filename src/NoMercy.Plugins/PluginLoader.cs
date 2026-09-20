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
using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Verification;
using NoMercy.Storage;

namespace NoMercy.Plugins;

/// <summary>
/// Loads plugin assemblies into their own <see cref="PluginLoadContext"/>, creates
/// the plugin instances through DI, and records the result in the
/// <see cref="IPluginRegistry"/>. Isolating this from <see cref="PluginManager"/>
/// keeps assembly-loading and AssemblyLoadContext lifecycle in one place.
/// </summary>
internal sealed class PluginLoader(
    IEventBus eventBus,
    IServiceProvider serviceProvider,
    ILogger logger,
    string pluginsPath,
    IStorage storage,
    IPluginRegistry registry,
    IPluginVerifier verifier,
    IPluginConsentService consentService,
    IPluginContextFactory contextFactory,
    PluginHostOptions? hostOptions = null
)
{
    private readonly IEventBus _eventBus = eventBus;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger _logger = logger;
    private readonly string _pluginsPath = pluginsPath;
    private readonly IStorage _storage = storage;
    private readonly IPluginRegistry _registry = registry;
    private readonly IPluginVerifier _verifier = verifier;
    private readonly IPluginConsentService _consentService = consentService;
    private readonly IPluginContextFactory _contextFactory = contextFactory;

    // The configured shared-assembly set, or the built-in one. Passing it here
    // is what makes PluginHostOptions.SharedAssemblies mean anything: the type
    // existed and documented itself as bindable from configuration, and every
    // load context was constructed without it, so the set was a hardcoded six
    // entries and adding one needed a code change.
    private readonly IReadOnlySet<string> _sharedAssemblies = (
        hostOptions ?? new PluginHostOptions()
    ).SharedAssemblies;

    /// <summary>
    /// Whether the plugin's own assembly carries an
    /// <see cref="IPluginServiceRegistrator"/>. Only that assembly is examined,
    /// so a plugin is not judged by what its dependencies happen to contain.
    /// </summary>
    private static bool DeclaresServiceRegistrator(IPlugin? instance)
    {
        if (instance is null)
            return false;

        try
        {
            return instance
                .GetType()
                .Assembly.GetExportedTypes()
                .Any(type =>
                    typeof(IPluginServiceRegistrator).IsAssignableFrom(type)
                    && type is { IsAbstract: false, IsInterface: false }
                );
        }
        catch (Exception)
        {
            // A plugin whose exported types cannot be walked (a missing
            // dependency behind one of them) is not a plugin that registers
            // services well enough to promise anything about.
            return false;
        }
    }

    internal async Task LoadPluginFromManifestAsync(
        string manifestPath,
        CancellationToken ct = default
    )
    {
        string pluginDir = Path.GetDirectoryName(manifestPath)!;

        try
        {
            PluginManifest manifest = await PluginManifestParser.ParseFileAsync(
                manifestPath,
                _storage,
                ct
            );
            ReportKindProblems(manifest, manifestPath);

            await ReportTranslationProblemsAsync(manifest, pluginDir, manifestPath, ct);

            string assemblyPath = _storage.CombinePath(pluginDir, manifest.Assembly);

            if (!await _storage.ExistsAsync(assemblyPath, ct))
            {
                _logger.LogWarning(
                    "Plugin manifest {ManifestPath} references assembly '{Assembly}' which was not found.",
                    [manifestPath, manifest.Assembly]
                );

                await _eventBus.PublishAsync(
                    new PluginErrorOccurredEvent
                    {
                        PluginId = manifest.Id.ToString(),
                        PluginName = manifest.Name,
                        ErrorMessage =
                            $"Assembly '{manifest.Assembly}' not found in plugin directory.",
                        ExceptionType = nameof(FileNotFoundException),
                    },
                    ct
                );

                return;
            }

            string absoluteAssemblyPath = ToLocalAssemblyPath(assemblyPath);

            // Manual drops carry no repository checksum; only ABI is enforced here.
            PluginVerificationResult verification = _verifier.Verify(
                manifest,
                absoluteAssemblyPath,
                null
            );

            if (!verification.Verified)
            {
                string failureMessage = string.Join("; ", verification.Failures);

                _logger.LogWarning(
                    "Plugin {PluginName} failed verification and was marked malfunctioned: {Failures}",
                    [manifest.Name, failureMessage]
                );

                PluginInfo malfunctionedInfo = PluginManifestParser.ToPluginInfo(
                    manifest,
                    assemblyPath,
                    PluginStatus.Malfunctioned,
                    manifestPath,
                    verification.Verified,
                    verification.Trusted
                );

                _registry[manifest.Id] = new(malfunctionedInfo, null, null);

                await _eventBus.PublishAsync(
                    new PluginErrorOccurredEvent
                    {
                        PluginId = manifest.Id.ToString(),
                        PluginName = manifest.Name,
                        ErrorMessage = failureMessage,
                        ExceptionType = nameof(PluginVerificationException),
                    },
                    ct
                );

                return;
            }

            // Loaded from a fresh copy, never from the installed folder: the
            // runtime keeps the image it mapped for a path while any old
            // context is alive, so an update loaded from the same path ran
            // the old code (Torrent Downloader 0.4.1 reported as 0.5.0).
            string loadPath = PluginShadowCopy.Create(_pluginsPath, absoluteAssemblyPath);
            string shadowDir = Path.GetDirectoryName(loadPath)!;

            PluginLoadContext loadContext = new(loadPath, _sharedAssemblies);

            try
            {
                Assembly assembly = loadContext.LoadFromAssemblyPath(loadPath);
                List<Type> pluginTypes = assembly
                    .GetTypes()
                    .Where(t =>
                        typeof(IPlugin).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false }
                    )
                    .ToList();

                bool foundPlugin = false;

                foreach (Type pluginType in pluginTypes)
                {
                    IPlugin? instance = PluginInstanceFactory.Create(_serviceProvider, pluginType);
                    if (instance is null)
                    {
                        continue;
                    }

                    // An elevated plugin (declares network/rest/ws/auth capabilities)
                    // must not silently start reaching the network or claims pipeline
                    // on first install — it loads but stays Disabled until the owner
                    // grants consent from the dashboard. Where it came from does
                    // not answer that question: see PluginAutoEnable.
                    bool mayAutoEnable = PluginAutoEnable.Allows(manifest, _consentService);

                    // A manifest that widened past what the owner already
                    // approved must not ride the old consent to Active — the
                    // owner said yes to a smaller request than this one.
                    if (PluginAutoEnable.NeedsReConsent(manifest, _consentService))
                    {
                        _logger.LogWarning(
                            "Plugin {PluginName} widened its capabilities past its recorded consent and was disabled pending re-consent.",
                            [manifest.Name]
                        );

                        await _eventBus.PublishAsync(
                            new PluginConsentRequiredEvent
                            {
                                PluginId = manifest.Id.ToString(),
                                PluginName = manifest.Name,
                                Reason = "capabilities-widened",
                            },
                            ct
                        );
                    }

                    PluginStatus initialStatus = mayAutoEnable
                        ? PluginStatus.Active
                        : PluginStatus.Disabled;

                    if (mayAutoEnable)
                    {
                        string dataFolder = Path.Combine(
                            _pluginsPath,
                            "data",
                            instance.Id.ToString()
                        );
                        if (!_storage.Exists(dataFolder))
                        {
                            _storage.CreateDirectory(dataFolder);
                        }

                        IPluginContext context = _contextFactory.Create(
                            instance.Id,
                            dataFolder,
                            _logger,
                            manifest.Capabilities,
                            instance.Name,
                            instance.Version
                        );

                        try
                        {
                            instance.Initialize(context);
                        }
                        catch (Exception ex)
                        {
                            initialStatus = PluginStatus.Malfunctioned;
                            instance.Dispose();

                            PluginInfo errorInfo = PluginManifestParser.ToPluginInfo(
                                manifest,
                                assemblyPath,
                                initialStatus,
                                manifestPath,
                                verification.Verified,
                                verification.Trusted
                            );

                            LoadedPlugin errorLoaded = new(errorInfo, null, loadContext, shadowDir);
                            _registry[manifest.Id] = errorLoaded;
                            foundPlugin = true;

                            await _eventBus.PublishAsync(
                                new PluginErrorOccurredEvent
                                {
                                    PluginId = manifest.Id.ToString(),
                                    PluginName = manifest.Name,
                                    ErrorMessage = ex.Message,
                                    ExceptionType = ex.GetType().Name,
                                },
                                ct
                            );

                            continue;
                        }
                    }

                    PluginInfo info = PluginManifestParser.ToPluginInfo(
                        manifest,
                        assemblyPath,
                        initialStatus,
                        manifestPath,
                        verification.Verified,
                        verification.Trusted
                    );

                    // Decides whether enabling this later can take full effect
                    // without a restart, so it is read from the assembly rather
                    // than taken on trust from the manifest.
                    info.ContributesServices = DeclaresServiceRegistrator(instance);

                    IPlugin? storedInstance =
                        initialStatus == PluginStatus.Active ? instance : null;

                    // Reaching here with no stored instance means the plugin
                    // was never auto-enabled — Initialize() was never called
                    // and `instance` is about to be discarded unreferenced.
                    // Dispose it now instead of leaking whatever its
                    // constructor allocated (the Malfunctioned/auto-enabled
                    // failure path above already disposes its own instance).
                    if (storedInstance is null)
                    {
                        instance.Dispose();
                    }

                    LoadedPlugin loaded = new(info, storedInstance, loadContext, shadowDir);
                    _registry[manifest.Id] = loaded;
                    foundPlugin = true;

                    if (initialStatus == PluginStatus.Active)
                    {
                        await _eventBus.PublishAsync(
                            new PluginLoadedEvent
                            {
                                PluginId = manifest.Id.ToString(),
                                PluginName = manifest.Name,
                                Version = manifest.Version,
                            },
                            ct
                        );
                    }
                }

                if (!foundPlugin)
                {
                    loadContext.Unload();
                    PluginShadowCopy.TryDelete(shadowDir);
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                string errorMessage = string.Join(
                    "; ",
                    ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message)
                );

                _logger.LogWarning(
                    "Failed to load plugin assembly {AssemblyPath}: {Error}",
                    [assemblyPath, errorMessage]
                );

                await _eventBus.PublishAsync(
                    new PluginErrorOccurredEvent
                    {
                        PluginId = manifest.Id.ToString(),
                        PluginName = manifest.Name,
                        ErrorMessage = errorMessage,
                        ExceptionType = nameof(ReflectionTypeLoadException),
                    },
                    ct
                );

                loadContext.Unload();
                PluginShadowCopy.TryDelete(shadowDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Failed to load plugin assembly {AssemblyPath}: {Error}",
                    [assemblyPath, ex.Message]
                );

                await _eventBus.PublishAsync(
                    new PluginErrorOccurredEvent
                    {
                        PluginId = manifest.Id.ToString(),
                        PluginName = manifest.Name,
                        ErrorMessage = ex.Message,
                        ExceptionType = ex.GetType().Name,
                    },
                    ct
                );

                loadContext.Unload();
                PluginShadowCopy.TryDelete(shadowDir);
            }
        }
        catch (Exception ex)
        {
            string pluginName = Path.GetFileName(pluginDir);

            _logger.LogWarning(
                "Failed to parse plugin manifest {ManifestPath}: {Error}",
                [manifestPath, ex.Message]
            );

            await _eventBus.PublishAsync(
                new PluginErrorOccurredEvent
                {
                    PluginId = Ulid.Empty.ToString(),
                    PluginName = pluginName,
                    ErrorMessage = $"Invalid plugin manifest: {ex.Message}",
                    ExceptionType = ex.GetType().Name,
                },
                ct
            );
        }
    }

    internal async Task LoadPluginAssemblyAsync(string assemblyPath, CancellationToken ct = default)
    {
        string absoluteAssemblyPath = ToLocalAssemblyPath(assemblyPath);
        PluginLoadContext loadContext;
        string loadPath;
        string shadowDir;
        try
        {
            // AssemblyDependencyResolver reads the assembly's .deps.json via the
            // native host. On Linux it throws for a DLL with no/invalid deps
            // manifest (a stray file, a plugin shipped without its deps);
            // Windows tolerates it. Constructing outside the try let that escape
            // and abort discovery of every other plugin — guard it so a bad
            // assembly is skipped and reported, not fatal.
            loadPath = PluginShadowCopy.Create(_pluginsPath, absoluteAssemblyPath);
            shadowDir = Path.GetDirectoryName(loadPath)!;
            loadContext = new(loadPath, _sharedAssemblies);
        }
        catch (Exception loadContextEx)
        {
            _logger.LogWarning(
                "Failed to initialize plugin load context for {AssemblyPath}: {Error}",
                [assemblyPath, loadContextEx.Message]
            );

            await _eventBus.PublishAsync(
                new PluginErrorOccurredEvent
                {
                    PluginId = Ulid.Empty.ToString(),
                    PluginName = Path.GetFileNameWithoutExtension(assemblyPath),
                    ErrorMessage =
                        $"Failed to initialize plugin load context: {loadContextEx.Message}",
                    ExceptionType = loadContextEx.GetType().Name,
                },
                ct
            );
            return;
        }

        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(loadPath);
            List<Type> pluginTypes = assembly
                .GetTypes()
                .Where(t =>
                    typeof(IPlugin).IsAssignableFrom(t)
                    && t is { IsAbstract: false, IsInterface: false }
                )
                .ToList();

            foreach (Type pluginType in pluginTypes)
            {
                // Isolate each plugin type: a single malfunctioning plugin —
                // including one whose constructor or Id/Name/etc. getters throw —
                // must never abort loading of the other plugin types discovered
                // in this assembly.
                IPlugin? instance = null;
                try
                {
                    instance = PluginInstanceFactory.Create(_serviceProvider, pluginType);
                    if (instance is null)
                    {
                        continue;
                    }

                    string dataFolder = Path.Combine(_pluginsPath, "data", instance.Id.ToString());
                    if (!await _storage.ExistsAsync(dataFolder, ct))
                    {
                        await _storage.CreateDirectoryAsync(dataFolder, ct);
                    }

                    // What the manifest said, if this plugin is already known.
                    // A reload reaches here with only the assembly — there is no
                    // plugin.json beside it to read — so everything the manifest
                    // carried would otherwise be dropped. Capabilities matter
                    // most: consent is decided by them, and an entry that lost
                    // them reads as needing none. Enabling a plugin that was
                    // waiting for approval therefore made it look approved,
                    // permanently, and the owner was left with a plugin the
                    // server would not run and no way to consent to it.
                    //
                    // Read before the context is built, not after: the context
                    // carries the network allowlist, so a context built with no
                    // capabilities denies every outbound host the manifest
                    // declared. Internet Radio relayed nothing and every station
                    // failed with PluginNetworkDeniedException, on a plugin whose
                    // manifest names the hosts it needs.
                    _registry.TryGetValue(instance.Id, out LoadedPlugin? known);

                    IPluginContext context = _contextFactory.Create(
                        instance.Id,
                        dataFolder,
                        _logger,
                        known?.Info.Capabilities,
                        instance.Name,
                        instance.Version
                    );

                    instance.Initialize(context);

                    PluginInfo info = new()
                    {
                        Id = instance.Id,
                        Name = instance.Name,
                        Description = instance.Description,
                        Version = instance.Version,
                        Status = PluginStatus.Active,
                        AssemblyPath = assemblyPath,
                        Capabilities = known?.Info.Capabilities,
                        Author = known?.Info.Author,
                        ProjectUrl = known?.Info.ProjectUrl,
                        TargetAbi = known?.Info.TargetAbi,
                    };

                    LoadedPlugin loaded = new(info, instance, loadContext, shadowDir);
                    _registry[instance.Id] = loaded;

                    await _eventBus.PublishAsync(
                        new PluginLoadedEvent
                        {
                            PluginId = instance.Id.ToString(),
                            PluginName = instance.Name,
                            Version = instance.Version.ToString(),
                        },
                        ct
                    );
                }
                catch (Exception ex)
                {
                    // The failure may itself be a throwing property getter, so
                    // read the plugin's identity defensively — building the error
                    // record must never re-enter a faulty getter and throw again.
                    SafePluginIdentity identity = SafePluginIdentity.Read(instance, pluginType);

                    _logger.LogError(
                        ex,
                        "Plugin {PluginName} in assembly {AssemblyPath} failed to load and was marked malfunctioned: {Error}",
                        [identity.Name, assemblyPath, ex.Message]
                    );

                    if (instance is not null)
                    {
                        try
                        {
                            instance.Dispose();
                        }
                        catch (Exception disposeEx)
                        {
                            _logger.LogWarning(
                                disposeEx,
                                "Plugin {PluginName} threw while being disposed after a load failure.",
                                identity.Name
                            );
                        }
                    }

                    PluginInfo info = new()
                    {
                        Id = identity.Id,
                        Name = identity.Name,
                        Description = identity.Description,
                        Version = identity.Version,
                        Status = PluginStatus.Malfunctioned,
                        AssemblyPath = assemblyPath,
                    };

                    LoadedPlugin loaded = new(info, null, loadContext, shadowDir);
                    if (identity.Id != Ulid.Empty)
                    {
                        _registry[identity.Id] = loaded;
                    }

                    await _eventBus.PublishAsync(
                        new PluginErrorOccurredEvent
                        {
                            PluginId = identity.Id.ToString(),
                            PluginName = identity.Name,
                            ErrorMessage = ex.Message,
                            ExceptionType = ex.GetType().Name,
                        },
                        ct
                    );
                }
            }

            if (pluginTypes.Count == 0)
            {
                loadContext.Unload();
                PluginShadowCopy.TryDelete(shadowDir);
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            string errorMessage = string.Join(
                "; ",
                ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message)
            );

            _logger.LogWarning(
                "Failed to load plugin assembly {AssemblyPath}: {Error}",
                [assemblyPath, errorMessage]
            );

            await _eventBus.PublishAsync(
                new PluginErrorOccurredEvent
                {
                    PluginId = Ulid.Empty.ToString(),
                    PluginName = assemblyName,
                    ErrorMessage = errorMessage,
                    ExceptionType = nameof(ReflectionTypeLoadException),
                },
                ct
            );

            loadContext.Unload();
            PluginShadowCopy.TryDelete(shadowDir);
        }
        catch (Exception ex)
        {
            string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

            _logger.LogWarning(
                "Failed to load plugin assembly {AssemblyPath}: {Error}",
                [assemblyPath, ex.Message]
            );

            await _eventBus.PublishAsync(
                new PluginErrorOccurredEvent
                {
                    PluginId = Ulid.Empty.ToString(),
                    PluginName = assemblyName,
                    ErrorMessage = ex.Message,
                    ExceptionType = ex.GetType().Name,
                },
                ct
            );

            loadContext.Unload();
            PluginShadowCopy.TryDelete(shadowDir);
        }
    }

    // AssemblyLoadContext and AssemblyDependencyResolver are raw filesystem APIs and need a
    // real absolute path. IStorage hands back paths relative to the plugins root, so resolve
    // them against the absolute local root before loading. An already-absolute path is returned
    // unchanged. Plugin assemblies are always local — they cannot be loaded from a remote driver.
    private string ToLocalAssemblyPath(string storagePath)
    {
        return Path.GetFullPath(Path.Combine(_pluginsPath, storagePath));
    }

    /// <summary>
    /// Checks a plugin's translations against the locale it was authored in.
    ///
    /// A warning rather than a refusal: a plugin missing one Dutch string is
    /// still a working plugin, and refusing to load it would punish every viewer
    /// for a gap that affects some of them. The author sees it on their own
    /// machine, which is the point.
    /// </summary>
    private async Task ReportTranslationProblemsAsync(
        PluginManifest manifest,
        string pluginDir,
        string manifestPath,
        CancellationToken ct
    )
    {
        if (manifest.Translations is null)
            return;

        string directory = Path.Combine(pluginDir, manifest.Translations.Path);
        Dictionary<string, string?> files = new();

        foreach (
            string locale in manifest
                .Translations.Locales.Append(manifest.Translations.Source)
                .Distinct()
        )
        {
            string path = Path.Combine(directory, $"{locale}.json");
            files[locale] = _storage.Exists(path)
                ? await _storage.ReadAllTextAsync(path, ct)
                : null;
        }

        List<PluginTranslationProblem> problems = PluginTranslationValidator.Validate(
            manifest.Translations,
            locale => files.GetValueOrDefault(locale)
        );

        foreach (PluginTranslationProblem problem in problems)
            _logger.LogWarning(
                "Plugin manifest {ManifestPath} has a translation problem: {Problem}",
                [manifestPath, problem.ToString()]
            );
    }

    /// <summary>
    /// Checks that every mount lands somewhere the clients actually place.
    ///
    /// A kind nobody recognises is quiet: the plugin loads, and its screen
    /// simply never appears anywhere.
    /// </summary>
    private void ReportKindProblems(PluginManifest manifest, string manifestPath)
    {
        foreach (PluginUiMount mount in manifest.Capabilities?.Ui?.Mounts ?? [])
        {
            if (!PluginKind.IsKnown(mount.Kind))
                _logger.LogWarning(
                    "Plugin manifest {ManifestPath} mounts '{Label}' as kind '{Kind}', which is not one this server places. It will not appear.",
                    [manifestPath, mount.Label, mount.Kind]
                );
        }
    }
}
