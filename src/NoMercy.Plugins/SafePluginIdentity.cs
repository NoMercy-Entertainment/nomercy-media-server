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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk;

/// <summary>
/// Reads identity metadata from a plugin instance whose property getters may
/// themselves throw. Failure isolation requires that a malfunctioning plugin can
/// never abort discovery of the other plugins — and that includes the error
/// reporting path: building a failure record must not re-enter a faulty getter
/// and throw again. Every read here is guarded and falls back to a safe default
/// derived from the plugin's CLR type.
/// </summary>
internal readonly record struct SafePluginIdentity(
    Ulid Id,
    string Name,
    string Description,
    Version Version
)
{
    private static readonly Version UnknownVersion = new(0, 0, 0);

    internal static SafePluginIdentity Read(IPlugin? instance, Type pluginType)
    {
        string name = pluginType.FullName ?? pluginType.Name;

        if (instance is null)
        {
            return new(Ulid.Empty, name, string.Empty, UnknownVersion);
        }

        Ulid id = Ulid.Empty;
        try
        {
            id = instance.Id;
        }
        catch (Exception)
        {
            // Faulty Id getter — fall back to Ulid.Empty.
        }

        try
        {
            string candidate = instance.Name;
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                name = candidate;
            }
        }
        catch (Exception)
        {
            // Faulty Name getter — keep the CLR type name.
        }

        string description = string.Empty;
        try
        {
            description = instance.Description ?? string.Empty;
        }
        catch (Exception)
        {
            // Faulty Description getter — leave it empty.
        }

        Version version = UnknownVersion;
        try
        {
            version = instance.Version ?? UnknownVersion;
        }
        catch (Exception)
        {
            // Faulty Version getter — fall back to 0.0.0.
        }

        return new(id, name, description, version);
    }
}
