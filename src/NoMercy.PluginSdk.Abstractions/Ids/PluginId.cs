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

using System.Text.Json.Serialization;

namespace NoMercy.PluginSdk.Abstractions;

[JsonConverter(typeof(PluginIdJsonConverter<PluginId>))]
public readonly record struct PluginId(Ulid Value)
{
    public static PluginId Empty { get; } = new(Ulid.Empty);

    /// <summary>
    /// Parses a manifest id written either as a Ulid or, on a manifest.json
    /// published before this platform settled on Ulid, as a GUID. The two
    /// forms are the same sixteen bytes, so a GUID id resolves to one fixed
    /// Ulid and the plugin keeps its identity, its stored consent and its
    /// grants across the change.
    /// </summary>
    public static PluginId Parse(string value)
    {
        if (Ulid.TryParse(value, out Ulid ulid))
        {
            return new PluginId(ulid);
        }

        return new PluginId(new Ulid(Guid.Parse(value)));
    }

    public static bool TryParse(string? value, out PluginId id)
    {
        if (value is not null && Ulid.TryParse(value, out Ulid parsed))
        {
            id = new PluginId(parsed);
            return true;
        }

        if (value is not null && Guid.TryParse(value, out Guid guid))
        {
            id = new PluginId(new Ulid(guid));
            return true;
        }

        id = Empty;
        return false;
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}
