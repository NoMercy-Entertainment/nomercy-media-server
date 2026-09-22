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

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;

namespace NoMercy.PluginSdk;

public class PluginConfiguration : IPluginConfiguration
{
    private readonly string _configFilePath;
    private readonly IStorage _storage;

    // SemaphoreSlim, not a plain lock object: the Async members below need to
    // hold this across an await, which a `lock` statement cannot do. The sync
    // and async members previously used DIFFERENT guards (this lock vs none
    // at all on the Async methods), so a sync Save/Get racing an async one
    // could interleave a read with a concurrent write.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // The Ulid converter is on the file, not on a property: the ids in here are
    // consent and grant records written by earlier versions, where a plugin id
    // was a GUID. Without it TryDeserialize below would catch the parse failure
    // and hand back "no config", which reads as a user who granted nothing —
    // every consented plugin silently back to disabled after an update.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new PluginIdJsonConverter() },
    };

    public PluginConfiguration(string dataFolderPath, IStorage storage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolderPath);
        ArgumentNullException.ThrowIfNull(storage);
        _configFilePath = Path.Combine(dataFolderPath, "config.json");
        _storage = storage;
    }

    public T? GetConfiguration<T>()
        where T : class, new()
    {
        _lock.Wait();
        try
        {
            if (!_storage.Exists(_configFilePath))
            {
                return null;
            }

            byte[] bytes = _storage.Read(_configFilePath);
            string json = Encoding.UTF8.GetString(bytes);
            return TryDeserialize<T>(json);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<T?> GetConfigurationAsync<T>(CancellationToken ct = default)
        where T : class, new()
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_storage.Exists(_configFilePath))
            {
                return null;
            }

            string json = await _storage.ReadAllTextAsync(_configFilePath, ct);
            return TryDeserialize<T>(json);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static T? TryDeserialize<T>(string json)
        where T : class, new()
    {
        // Plugin config files can drift to malformed JSON across upgrades or
        // crashes mid-write. Treating that as 'no config' lets the plugin
        // re-initialise with defaults instead of taking the load path down
        // with a JsonException.
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SaveConfiguration<T>(T configuration)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _lock.Wait();
        try
        {
            string? directory = Path.GetDirectoryName(_configFilePath);
            if (directory is not null && !_storage.Exists(directory))
            {
                _storage.CreateDirectory(directory);
            }

            string json = Merge(ReadExisting(), configuration);
            _storage.Write(_configFilePath, Encoding.UTF8.GetBytes(json));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveConfigurationAsync<T>(T configuration, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(configuration);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(_configFilePath);
            if (directory is not null && !_storage.Exists(directory))
            {
                _storage.CreateDirectory(directory);
            }

            string json = Merge(ReadExisting(), configuration);
            await _storage.WriteAllTextAsync(_configFilePath, json, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string? ReadExisting() =>
        _storage.Exists(_configFilePath)
            ? Encoding.UTF8.GetString(_storage.Read(_configFilePath))
            : null;

    /// <summary>
    /// One file, several records: consent, grants and secrets are all written
    /// here by different types, and serializing one of them over the whole file
    /// dropped the others. Consenting to a plugin wiped its grants, so the host
    /// it had just been allowed came straight back as a request the owner had
    /// already answered.
    /// <para>
    /// Keys the incoming record declares win; every other key is left as it was.
    /// A file this cannot parse is replaced rather than merged into - the same
    /// answer <see cref="TryDeserialize{T}"/> gives a malformed file.
    /// </para>
    /// </summary>
    private static string Merge<T>(string? existing, T configuration)
        where T : class
    {
        string json = JsonSerializer.Serialize(configuration, JsonOptions);

        if (string.IsNullOrWhiteSpace(existing))
            return json;

        try
        {
            if (
                JsonNode.Parse(existing) is not JsonObject document
                || JsonNode.Parse(json) is not JsonObject incoming
            )
                return json;

            foreach (KeyValuePair<string, JsonNode?> property in incoming.ToList())
            {
                incoming.Remove(property.Key);
                document[property.Key] = property.Value;
            }

            return document.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public bool HasConfiguration()
    {
        return _storage.Exists(_configFilePath);
    }

    public void DeleteConfiguration()
    {
        _lock.Wait();
        try
        {
            if (_storage.Exists(_configFilePath))
            {
                _storage.Delete(_configFilePath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
