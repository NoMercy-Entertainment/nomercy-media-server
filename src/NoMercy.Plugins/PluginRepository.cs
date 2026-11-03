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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;

namespace NoMercy.PluginSdk;

public class PluginRepository : IPluginRepository
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _repositoriesFilePath;
    private readonly string _declinedDefaultFilePath;
    private readonly string _catalogueFilePath;
    private readonly IStorage _storage;
    private readonly List<PluginRepositoryInfo> _repositories = [];
    private readonly List<PluginRepositoryEntry> _availablePlugins = [];
    private readonly Dictionary<string, List<PluginRepositoryEntry>> _pluginsByRepository = [];

    /// <summary>
    /// The plugins a trusted repository lists, rebuilt on every load and refresh.
    /// <para>
    /// Kept apart from the entries themselves because an entry says what a
    /// plugin is and this says where it came from — and the second one is the
    /// only thing a trust decision may rest on.
    /// </para>
    /// </summary>
    private readonly HashSet<Ulid> _trustedPluginIds = [];
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public PluginRepository(
        HttpClient httpClient,
        ILogger logger,
        string pluginsPath,
        IStorage storage
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsPath);
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));

        string configDir = Path.Combine(pluginsPath, "configurations");

        if (!_storage.Exists(configDir))
        {
            _storage.CreateDirectory(configDir);
        }

        _repositoriesFilePath = Path.Combine(configDir, "repositories.json");
        _declinedDefaultFilePath = Path.Combine(configDir, "default-repository-removed");
        _catalogueFilePath = Path.Combine(configDir, "repository-catalogue.json");
    }

    /// <summary>
    /// Creates a repository and asynchronously loads any persisted repository
    /// list from disk. Prefer this over the constructor: the previous ctor read
    /// the file sync-over-async (.GetAwaiter().GetResult()), which can deadlock
    /// during DI resolution under a synchronization context.
    /// </summary>
    public static async Task<PluginRepository> CreateAsync(
        HttpClient httpClient,
        ILogger logger,
        string pluginsPath,
        IStorage storage,
        CancellationToken ct = default
    )
    {
        PluginRepository repository = new(httpClient, logger, pluginsPath, storage);
        await repository.LoadAsync(ct);
        return repository;
    }

    /// <summary>
    /// Reads the repository list, then the catalogue the last refresh cached.
    /// <para>
    /// The catalogue is what tells a trusted repository's plugins apart, and a
    /// refresh needs the internet. Reading the cached one here means a server
    /// knows what its trusted repositories list from the moment it starts, also
    /// when nomercy.tv cannot be reached; the next refresh replaces it.
    /// </para>
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await LoadRepositoriesFromDiskAsync(ct);
        await LoadCatalogueFromDiskAsync(ct);
    }

    public IReadOnlyList<PluginRepositoryInfo> GetRepositories()
    {
        lock (_lock)
        {
            return _repositories.ToList().AsReadOnly();
        }
    }

    public async Task AddRepositoryAsync(string name, string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        lock (_lock)
        {
            if (_repositories.Any(r => r.Name == name))
            {
                throw new InvalidOperationException($"Repository '{name}' already exists.");
            }

            _repositories.Add(
                new()
                {
                    Name = name,
                    Url = url,
                    Enabled = true,
                }
            );
        }

        await SaveRepositoriesToDiskAsync(ct);
        await RefreshRepositoryAsync(name, url, ct);
    }

    public async Task RemoveRepositoryAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        bool removedDefault;
        lock (_lock)
        {
            removedDefault = _repositories.Any(r =>
                r.Name == name && r.Url == DefaultRepositoryUrl
            );

            int removed = _repositories.RemoveAll(r => r.Name == name);
            if (removed == 0)
            {
                throw new InvalidOperationException($"Repository '{name}' not found.");
            }

            _pluginsByRepository.Remove(name);
            RebuildAvailablePlugins();
            RebuildTrustedSet();
        }

        if (removedDefault)
        {
            await RecordDefaultRemovedAsync(ct);
        }

        await SaveRepositoriesToDiskAsync(ct);
    }

    public async Task SetRepositoryTrustAsync(
        string name,
        bool trusted,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_lock)
        {
            PluginRepositoryInfo? repository = _repositories.FirstOrDefault(r => r.Name == name);

            if (repository is null)
            {
                throw new InvalidOperationException($"Repository '{name}' not found.");
            }

            repository.Trusted = trusted;

            // Recomputed from what every repository listed last, at once: an
            // owner who grants trust or takes it back means now, not at the
            // next refresh, and only for this repository.
            RebuildTrustedSet();
        }

        await SaveRepositoriesToDiskAsync(ct);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        List<PluginRepositoryInfo> repos;
        lock (_lock)
        {
            repos = [.. _repositories.Where(r => r.Enabled)];
        }

        Dictionary<string, List<PluginRepositoryEntry>> refreshed = [];

        foreach (PluginRepositoryInfo repo in repos)
        {
            try
            {
                refreshed[repo.Name] = await FetchRepositoryPluginsAsync(repo.Url, ct);
            }
            catch (Exception ex)
            {
                // The repository keeps what it listed last: an index that cannot
                // be reached right now has not stopped listing its plugins.
                _logger.LogWarning(
                    "Failed to refresh repository '{Name}' ({Url}): {Error}",
                    [repo.Name, repo.Url, ex.Message]
                );
            }
        }

        lock (_lock)
        {
            foreach (KeyValuePair<string, List<PluginRepositoryEntry>> entry in refreshed)
            {
                _pluginsByRepository[entry.Key] = entry.Value;
            }

            RebuildAvailablePlugins();
            RebuildTrustedSet();
        }

        if (refreshed.Count > 0)
        {
            await SaveCatalogueToDiskAsync(ct);
        }
    }

    public bool IsFromTrustedRepository(Ulid pluginId)
    {
        lock (_lock)
        {
            return _trustedPluginIds.Contains(pluginId);
        }
    }

    public IReadOnlyList<PluginRepositoryEntry> GetAvailablePlugins()
    {
        lock (_lock)
        {
            return _availablePlugins.ToList().AsReadOnly();
        }
    }

    public PluginRepositoryEntry? FindPlugin(Ulid pluginId)
    {
        lock (_lock)
        {
            return _availablePlugins.FirstOrDefault(p => p.Id == pluginId);
        }
    }

    public PluginVersionEntry? FindVersion(Ulid pluginId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        lock (_lock)
        {
            PluginRepositoryEntry? plugin = _availablePlugins.FirstOrDefault(p => p.Id == pluginId);
            return plugin?.Versions.FirstOrDefault(v => v.Version == version);
        }
    }

    internal async Task<List<PluginRepositoryEntry>> FetchRepositoryPluginsAsync(
        string url,
        CancellationToken ct = default
    )
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        PluginRepositoryManifest? manifest = JsonSerializer.Deserialize<PluginRepositoryManifest>(
            json,
            JsonOptions
        );

        if (manifest is null)
        {
            return [];
        }

        return manifest.Plugins;
    }

    private async Task RefreshRepositoryAsync(string name, string url, CancellationToken ct)
    {
        try
        {
            List<PluginRepositoryEntry> plugins = await FetchRepositoryPluginsAsync(url, ct);

            lock (_lock)
            {
                _pluginsByRepository[name] = plugins;
                _availablePlugins.AddRange(plugins);
            }

            await SaveCatalogueToDiskAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to fetch repository '{Name}' ({Url}): {Error}",
                [name, url, ex.Message]
            );
        }
    }

    private const string DefaultRepositoryName = "NoMercy Marketplace";
    private const string DefaultRepositoryUrl = "https://api.nomercy.tv/v1/marketplace/index.json";

    /// <summary>
    /// The index every server reads unless its owner removes it. A new instance
    /// each time, because trust is toggled on the stored object.
    /// </summary>
    private static PluginRepositoryInfo NewDefaultRepository() =>
        new()
        {
            Name = DefaultRepositoryName,
            Url = DefaultRepositoryUrl,
            // Someone running this server has already decided to trust what we
            // publish. Trust is provenance only: a plugin listed here still waits
            // for the owner's consent before it runs.
            Trusted = true,
        };

    /// <summary>
    /// Reads the persisted list. Public so startup awaits it once, instead of
    /// the synchronous container resolve blocking a thread on disk.
    /// </summary>
    public async Task LoadRepositoriesFromDiskAsync(CancellationToken ct = default)
    {
        bool declined = _storage.Exists(_declinedDefaultFilePath);

        if (!_storage.Exists(_repositoriesFilePath))
        {
            lock (_lock)
            {
                if (_repositories.Count == 0 && !declined)
                {
                    _repositories.Add(NewDefaultRepository());
                }
            }

            await SaveRepositoriesToDiskAsync(ct);
            return;
        }

        try
        {
            string json = await _storage.ReadAllTextAsync(_repositoriesFilePath, ct);
            List<PluginRepositoryInfo>? repos = JsonSerializer.Deserialize<
                List<PluginRepositoryInfo>
            >(json, JsonOptions);
            if (repos is not null)
            {
                _repositories.AddRange(repos);
            }

            bool moved = MoveOffTheRetiredIndex();
            bool seeded = !declined && SeedDefaultRepository();

            if (moved || seeded)
            {
                await SaveRepositoriesToDiskAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to load repositories from {Path}: {Error}",
                [_repositoriesFilePath, ex.Message]
            );
        }
    }

    /// <summary>
    /// The seeded index moved from a file on GitHub to the marketplace, which
    /// signs what it lists and publishes revocations. Rewritten in place on a
    /// server that still carries the old one, so an owner who never touched the
    /// list keeps reading a store that has releases in it.
    /// <para>
    /// Only our own retired address is matched, and only its url changes. An
    /// owner who removed it stays without it, and a repository they added
    /// themselves is left alone.
    /// </para>
    /// </summary>
    private bool MoveOffTheRetiredIndex()
    {
        const string retired =
            "https://raw.githubusercontent.com/NoMercy-Entertainment/nomercy-plugins/master/index.json?v=1";

        lock (_lock)
        {
            int at = _repositories.FindIndex(repository => repository.Url == retired);

            if (at < 0)
                return false;

            _repositories[at] = NewDefaultRepository();

            return true;
        }
    }

    /// <summary>Adds the default unless its url or name is listed already.</summary>
    private bool SeedDefaultRepository()
    {
        lock (_lock)
        {
            if (
                _repositories.Any(repository =>
                    repository.Url == DefaultRepositoryUrl
                    || repository.Name == DefaultRepositoryName
                )
            )
                return false;

            _repositories.Add(NewDefaultRepository());

            return true;
        }
    }

    private async Task RecordDefaultRemovedAsync(CancellationToken ct)
    {
        try
        {
            await _storage.WriteAllTextAsync(_declinedDefaultFilePath, "removed", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to record the removed default repository at {Path}: {Error}",
                [_declinedDefaultFilePath, ex.Message]
            );
        }
    }

    /// <summary>
    /// The catalogue: what every enabled repository listed last. The caller
    /// holds <see cref="_lock"/>.
    /// </summary>
    private void RebuildAvailablePlugins()
    {
        _availablePlugins.Clear();

        foreach (PluginRepositoryInfo repository in _repositories.Where(r => r.Enabled))
        {
            if (
                _pluginsByRepository.TryGetValue(
                    repository.Name,
                    out List<PluginRepositoryEntry>? plugins
                )
            )
                _availablePlugins.AddRange(plugins);
        }
    }

    /// <summary>
    /// The trusted set, from each enabled repository's trust flag and what it
    /// listed last. The caller holds <see cref="_lock"/>.
    /// <para>
    /// Recomputed rather than adjusted: a plugin dropped from a trusted index,
    /// or a repository the owner stopped trusting, has to stop being trusted
    /// here too, and adding to the old set would keep it forever.
    /// </para>
    /// </summary>
    private void RebuildTrustedSet()
    {
        _trustedPluginIds.Clear();

        foreach (
            PluginRepositoryInfo repository in _repositories.Where(r => r.Enabled && r.Trusted)
        )
        {
            if (
                _pluginsByRepository.TryGetValue(
                    repository.Name,
                    out List<PluginRepositoryEntry>? plugins
                )
            )
                _trustedPluginIds.UnionWith(plugins.Select(plugin => plugin.Id));
        }
    }

    /// <summary>
    /// Reads what each repository listed at the last refresh, so trust and the
    /// catalogue survive a restart. Keyed by the repository's address: an entry
    /// counts only for a repository that still points where it was read from.
    /// </summary>
    private async Task LoadCatalogueFromDiskAsync(CancellationToken ct)
    {
        if (!_storage.Exists(_catalogueFilePath))
            return;

        try
        {
            string json = await _storage.ReadAllTextAsync(_catalogueFilePath, ct);
            Dictionary<string, List<PluginRepositoryEntry>>? byUrl = JsonSerializer.Deserialize<
                Dictionary<string, List<PluginRepositoryEntry>>
            >(json, JsonOptions);

            if (byUrl is null)
                return;

            lock (_lock)
            {
                foreach (PluginRepositoryInfo repository in _repositories)
                {
                    // A refresh that already answered is newer than the file.
                    if (_pluginsByRepository.ContainsKey(repository.Name))
                        continue;

                    if (byUrl.TryGetValue(repository.Url, out List<PluginRepositoryEntry>? plugins))
                        _pluginsByRepository[repository.Name] = plugins;
                }

                RebuildAvailablePlugins();
                RebuildTrustedSet();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to load the cached repository catalogue from {Path}: {Error}",
                [_catalogueFilePath, ex.Message]
            );
        }
    }

    private async Task SaveCatalogueToDiskAsync(CancellationToken ct)
    {
        try
        {
            Dictionary<string, List<PluginRepositoryEntry>> byUrl = [];
            lock (_lock)
            {
                foreach (PluginRepositoryInfo repository in _repositories)
                {
                    if (
                        _pluginsByRepository.TryGetValue(
                            repository.Name,
                            out List<PluginRepositoryEntry>? plugins
                        )
                    )
                        byUrl[repository.Url] = [.. plugins];
                }
            }

            string json = JsonSerializer.Serialize(byUrl, JsonOptions);
            await _storage.WriteAllTextAsync(_catalogueFilePath, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to save the repository catalogue to {Path}: {Error}",
                [_catalogueFilePath, ex.Message]
            );
        }
    }

    private async Task SaveRepositoriesToDiskAsync(CancellationToken ct)
    {
        try
        {
            // AddRepositoryAsync / RemoveRepositoryAsync mutate _repositories
            // under `lock (_lock)` but call this AFTER releasing it — reading
            // the live list here (as this used to) could race a concurrent
            // mutation and throw "Collection was modified" or serialize a
            // half-updated list. Snapshot under the lock; serialize + write
            // outside it so file I/O never blocks the other lock holders.
            List<PluginRepositoryInfo> snapshot;
            lock (_lock)
            {
                snapshot = [.. _repositories];
            }

            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await _storage.WriteAllTextAsync(_repositoriesFilePath, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to save repositories to {Path}: {Error}",
                [_repositoriesFilePath, ex.Message]
            );
        }
    }
}
