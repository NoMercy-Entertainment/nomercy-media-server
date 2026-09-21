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
/// Everything one plugin holds about one person, in a form that person can be
/// handed.
/// <para>
/// JSON rather than the plugin's own storage format, because the point is that
/// the reader can open it. An export only the plugin can read answers the
/// request on paper and not in fact.
/// </para>
/// </summary>
public sealed record PluginUserScopeExport
{
    public required PluginId Plugin { get; init; }
    public required UserId User { get; init; }
    public required DateTimeOffset TakenAt { get; init; }

    /// <summary>The scope's contents, as JSON.</summary>
    public required string Json { get; init; }

    /// <summary>
    /// Files held for this person, by the name the plugin stored them under.
    /// Named rather than inlined, so an export of a large download does not
    /// have to be built in memory before anyone can read it.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}
