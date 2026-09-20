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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Capabilities;

/// <summary>
/// How often each refusal fired, per plugin.
/// <para>
/// A refusal that fires once is an author learning the rule. The same one
/// firing ten thousand times is a plugin in a retry loop, and the owner sees
/// only that it is slow. Counting is what turns the second into something the
/// dashboard can say out loud.
/// </para>
/// </summary>
public interface IPluginRefusalCounter
{
    void Count(Ulid pluginId, PluginRefusal refusal);

    /// <summary>What this plugin has been refused, by code, most frequent first.</summary>
    IReadOnlyList<KeyValuePair<string, int>> Counts(Ulid pluginId);

    void Clear(Ulid pluginId);
}
