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
using NoMercy.Events;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Access;

namespace NoMercy.PluginSdk.Entitlements;

/// <summary>
/// The bundle on disk, so a server that starts offline still knows what it was
/// last told it may run.
/// </summary>
public class PluginEntitlementStore(string? folder = null, IEventBus? events = null)
    : IPluginEntitlementStore
{
    private readonly string _folder = folder ?? AppFiles.PluginConfigPath;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string File => System.IO.Path.Combine(_folder, "entitlements.json");

    private PluginEntitlementBundle? _cached;

    public PluginEntitlementBundle Current
    {
        get
        {
            if (_cached is not null)
                return _cached;

            if (!System.IO.File.Exists(File))
                return PluginEntitlementBundle.None;

            try
            {
                _cached =
                    JsonSerializer.Deserialize<PluginEntitlementBundle>(
                        System.IO.File.ReadAllText(File),
                        Json
                    ) ?? PluginEntitlementBundle.None;
            }
            catch (JsonException)
            {
                // A file we cannot read is a server that has not asked. An
                // empty bundle would read as "the owner bought nothing" on the
                // strength of a corrupt file, and take away what they paid for.
                _cached = PluginEntitlementBundle.None;
            }

            return _cached;
        }
    }

    public void Save(PluginEntitlementBundle replacement)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(replacement, Json));
        _cached = replacement;

        // A list names what it now blocks, never what it stopped blocking, so
        // every account is told its answer rather than a guess at whose changed.
        PluginAccessAnnouncement.Changed(events);
    }
}
