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
using NoMercy.NmSystem.Information;

namespace NoMercy.Plugins.Guests;

/// <summary>
/// The guest installs on disk, so a restart does not turn one person's plugin
/// into the server's.
/// </summary>
public class PluginGuestInstallStore(string? folder = null) : IPluginGuestInstallStore
{
    private readonly string _folder = folder ?? AppFiles.PluginConfigPath;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string File => Path.Combine(_folder, "guest-installs.json");

    private Dictionary<string, Guid>? _cached;

    private Dictionary<string, Guid> Current
    {
        get
        {
            if (_cached is not null)
                return _cached;

            if (!System.IO.File.Exists(File))
                return _cached = [];

            try
            {
                _cached =
                    JsonSerializer.Deserialize<Dictionary<string, Guid>>(
                        System.IO.File.ReadAllText(File),
                        Json
                    ) ?? [];
            }
            catch (JsonException)
            {
                // Unreadable means nothing is recorded as a guest's, which
                // makes those installs the server's and visible to the owner.
                // The owner can then remove what they do not recognise; the
                // other way round hides a plugin from everyone including them.
                _cached = [];
            }

            return _cached;
        }
    }

    public void Record(Ulid pluginId, Guid guestId)
    {
        Current[pluginId.ToString()] = guestId;
        Flush();
    }

    public Guid? GuestFor(Ulid pluginId) =>
        Current.TryGetValue(pluginId.ToString(), out Guid guest) ? guest : null;

    public IReadOnlyList<Ulid> PluginsFor(Guid guestId) =>
        [.. Current.Where(entry => entry.Value == guestId).Select(entry => Ulid.Parse(entry.Key))];

    public void Forget(Ulid pluginId)
    {
        if (Current.Remove(pluginId.ToString()))
            Flush();
    }

    private void Flush()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(Current, Json));
    }
}
