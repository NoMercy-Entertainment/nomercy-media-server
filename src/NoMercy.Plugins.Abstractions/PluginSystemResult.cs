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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// What came back.
/// </summary>
public class PluginSystemResult
{
    public required bool Ok { get; init; }

    /// <summary>Set when the plugin was not allowed, rather than when it failed.</summary>
    public bool Refused { get; init; }

    /// <summary>Set when the host does not offer this capability at all.</summary>
    public bool Unsupported { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, object?> Data { get; init; } =
        new Dictionary<string, object?>();

    public static PluginSystemResult Done(IReadOnlyDictionary<string, object?>? data = null)
    {
        return new() { Ok = true, Data = data ?? new Dictionary<string, object?>() };
    }

    public static PluginSystemResult NotAllowed(string capability)
    {
        return new()
        {
            Ok = false,
            Refused = true,
            Reason = $"no grant for '{PluginCapability.GrantFor(capability)}'",
        };
    }

    public static PluginSystemResult NotOffered(string capability)
    {
        return new()
        {
            Ok = false,
            Unsupported = true,
            Reason = $"this host offers no '{capability}'",
        };
    }

    public static PluginSystemResult Failed(string reason)
    {
        return new() { Ok = false, Reason = reason };
    }
}
