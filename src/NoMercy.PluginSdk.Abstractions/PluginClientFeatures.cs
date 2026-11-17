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

/// <summary>
/// The parts of the view contract the asking client says it can run.
/// <para>
/// A client sends them as one comma-separated query value,
/// <c>features=paging</c>. A client that sends nothing is an older client. The
/// shelves look the same to every client; what changes is whether a
/// <see cref="PluginActionType.LoadPage" /> does anything. A plugin that used to
/// send one long page can keep sending it to a client without paging, and send
/// the first page and a next cursor to a client with it.
/// </para>
/// </summary>
public sealed class PluginClientFeatures
{
    /// <summary>The query key the client lists its features under.</summary>
    public const string QueryKey = "features";

    /// <summary>The client runs <see cref="PluginActionType.LoadPage" /> and reads a shelf's <c>loadMore</c>.</summary>
    public const string Paging = "paging";

    private readonly HashSet<string> _names;

    private PluginClientFeatures(IEnumerable<string> names)
    {
        _names = new(names, StringComparer.Ordinal);
    }

    /// <summary>What a client that sends no feature list can run: nothing new.</summary>
    public static PluginClientFeatures None { get; } = new([]);

    /// <summary>Every feature the client named, as it spelled them.</summary>
    public IReadOnlyCollection<string> Names => _names;

    /// <summary>Whether the client said it can run <paramref name="feature" />.</summary>
    public bool Supports(string feature) => _names.Contains(feature);

    /// <summary>A client that runs exactly <paramref name="features" />. For tests and hosts.</summary>
    public static PluginClientFeatures Of(params string[] features) => new(features);

    /// <summary>
    /// The features in a view request's query. Empty, blank and repeated names
    /// are dropped, and case is kept: <c>Paging</c> is not a feature.
    /// </summary>
    public static PluginClientFeatures From(IReadOnlyDictionary<string, string> query)
    {
        if (!query.TryGetValue(QueryKey, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return None;

        return new(
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        );
    }
}
