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
using System.Text.Json.Serialization;
using NoMercy.Events;
using NoMercy.NmSystem.Information;
using NoMercy.Plugins.Access;

namespace NoMercy.Plugins.Revocation;

/// <summary>
/// The list on disk, so a server that starts offline still knows what it was
/// last told. Kept beside the other plugin configuration rather than in the
/// database: the gate runs before anything else is up.
/// </summary>
public class PluginRevocationStore(string? folder = null, IEventBus? events = null)
    : IPluginRevocationStore
{
    private readonly string _folder = folder ?? AppFiles.PluginConfigPath;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private string File => System.IO.Path.Combine(_folder, "revocations.json");

    private PluginRevocationList? _cached;

    public PluginRevocationList Current
    {
        get
        {
            if (_cached is not null)
                return _cached;

            if (!System.IO.File.Exists(File))
                return PluginRevocationList.None;

            try
            {
                _cached =
                    JsonSerializer.Deserialize<PluginRevocationList>(
                        System.IO.File.ReadAllText(File),
                        Json
                    ) ?? PluginRevocationList.None;
            }
            catch (JsonException)
            {
                // A file we cannot read is a server that has not heard, which
                // pauses. Treating it as an empty list would say "nothing is
                // revoked" on the strength of a corrupt file.
                _cached = PluginRevocationList.None;
            }

            return _cached;
        }
    }

    public void Save(PluginRevocationList replacement)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(replacement, Json));
        _cached = replacement;

        // A list names what it now blocks, never what it stopped blocking, so
        // every account is told its answer rather than a guess at whose changed.
        PluginAccessAnnouncement.Changed(events);
    }
}
