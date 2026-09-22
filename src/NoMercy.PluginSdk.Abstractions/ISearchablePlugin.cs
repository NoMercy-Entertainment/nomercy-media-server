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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>One thing a plugin found, in the words every client already draws.</summary>
public sealed record PluginSearchResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public Uri? Cover { get; init; }

    /// <summary>A route in the plugin's own table, opened as any other placement is.</summary>
    public required string Route { get; init; }

    public IReadOnlyDictionary<string, string> Params { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>What one plugin answered a query with.</summary>
public sealed record PluginSearchGroup(
    string PluginId,
    string PluginName,
    IReadOnlyList<PluginSearchResult> Results
);

/// <summary>
/// A plugin that can answer the search box.
///
/// <para>
/// It sees the query and the caller, and nothing else about the person typing:
/// a search box is where someone types the name of a thing they have not told
/// anyone they want, so a plugin gets the words and no history.
/// </para>
///
/// <para>
/// The host asks every plugin at once and stops waiting when its own deadline
/// passes. One slow provider was one slow search box for everyone, including
/// the library results that were ready immediately.
/// </para>
/// </summary>
public interface ISearchablePlugin : IPlugin
{
    Task<IReadOnlyList<PluginSearchResult>> SearchAsync(
        string query,
        PluginCaller caller,
        CancellationToken ct
    );
}
