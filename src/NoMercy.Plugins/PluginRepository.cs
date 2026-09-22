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
    private readonly IStorage _storage;
    private readonly List<PluginRepositoryInfo> _repositories = [];
    private readonly List<PluginRepositoryEntry> _availablePlugins = [];

    /// <summary>
    /// The plugins a trusted repository lists, rebuilt on every refresh.
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
        await repository.LoadRepositoriesFromDiskAsync(ct);
        return repository;
    }

    public Task LoadAsync(CancellationToken ct = default)
    {
        return LoadRepositoriesFromDiskAsync(ct);
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

        lock (_lock)
        {
            int removed = _repositories.RemoveAll(r => r.Name == name);
            if (removed == 0)
            {
                throw new InvalidOperationException($"Repository '{name}' not found.");
            }
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

            // Cleared rather than adjusted. An entry does not carry which index
            // listed it, so the only honest answer until the next refresh
            // rebuilds the set is that nothing is trusted — and an owner who
            // takes trust back means now, not at the next refresh.
            _trustedPluginIds.Clear();
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

        List<PluginRepositoryEntry> allPlugins = [];
        HashSet<Ulid> trusted = [];

        foreach (PluginRepositoryInfo repo in repos)
        {
            try
            {
                List<PluginRepositoryEntry> plugins = await FetchRepositoryPluginsAsync(
                    repo.Url,
                    ct
                );
                allPlugins.AddRange(plugins);

                if (repo.Trusted)
                    trusted.UnionWith(plugins.Select(plugin => plugin.Id));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Failed to refresh repository '{Name}' ({Url}): {Error}",
                    [repo.Name, repo.Url, ex.Message]
                );
            }
        }

        lock (_lock)
        {
            _availablePlugins.Clear();
            _availablePlugins.AddRange(allPlugins);

            // Replaced rather than merged: a plugin dropped from a trusted index,
            // or a repository the owner stopped trusting, has to stop being
            // trusted here too, and a merge would keep it forever.
            _trustedPluginIds.Clear();
            _trustedPluginIds.UnionWith(trusted);
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
                _availablePlugins.AddRange(plugins);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to fetch repository '{Name}' ({Url}): {Error}",
                [name, url, ex.Message]
            );
        }
    }

    /// <summary>
    /// Reads the persisted repository list. Public because the container builds
    /// this singleton synchronously and the file read is async: startup calls
    /// this once, rather than the resolve blocking a thread on disk.
    /// </summary>
    /// <summary>
    /// The index every server reads unless its owner removes it. Seeded on a
    /// server that has never had a repository list, rather than hard-coded into
    /// lookups: it lands in the same file every other repository lives in, so
    /// removing it is the ordinary Remove and it stays removed.
    /// </summary>
    private static readonly PluginRepositoryInfo DefaultRepository = new()
    {
        Name = "NoMercy Marketplace",
        Url = "https://api.nomercy.tv/v1/marketplace/index.json",
        // Someone running this server has already decided to trust what we
        // publish. Trust is provenance only: a plugin listed here still waits
        // for the owner's consent before it runs.
        Trusted = true,
    };

    public async Task LoadRepositoriesFromDiskAsync(CancellationToken ct = default)
    {
        if (!_storage.Exists(_repositoriesFilePath))
        {
            lock (_lock)
            {
                if (_repositories.Count == 0)
                {
                    _repositories.Add(DefaultRepository);
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

            if (MoveOffTheRetiredIndex())
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

            _repositories[at] = DefaultRepository;

            return true;
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
