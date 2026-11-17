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

using NoMercy.Design;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Holds a plugin's shelves to the paging caps before the view is sent, and
/// answers an append with the one shelf it was for.
/// <para>
/// The SDK builders already keep to the caps. This is for a shelf written by
/// hand, or by a plugin that ignores the limit it was asked for: a grid is
/// sent with at most <see cref="PluginPaging.MaximumPageSize" /> items, a row
/// with at most <see cref="PluginPaging.MaximumRowCards" /> cards, a page with
/// at most <see cref="PluginPaging.MaximumPageRows" /> rows, and an appended
/// page with no more than the limit the client asked for.
/// </para>
/// <para>
/// It builds a new view rather than changing the plugin's: a plugin that keeps
/// one tree and returns it twice must not find it trimmed the second time.
/// </para>
/// </summary>
public static class PluginShelfCaps
{
    private const string ItemsKey = "items";

    /// <summary>
    /// The view as it may be sent. <paramref name="trimmed" /> counts the items
    /// left out, so the host can say a plugin went over.
    /// <para>
    /// For an append (<see cref="PluginPageRequest.IsAppend" />) the view holds
    /// only the shelf whose id is <see cref="PluginPageRequest.Append" />, or no
    /// component at all when the plugin did not send it. No component means no
    /// more pages.
    /// </para>
    /// </summary>
    public static PluginView Apply(PluginView view, PluginPageRequest page, out int trimmed)
    {
        trimmed = 0;

        if (view.Components is null)
            return view;

        List<(PluginComponent Node, int? Limit)> roots = page.Append is { } target
            ? Find(view.Components, target) is { } shelf
                ? [(shelf, page.Limit)]
                : []
            : [.. view.Components.Select(node => (node, (int?)null))];

        List<PluginComponent> kept = [];
        foreach ((PluginComponent node, int? limit) in roots)
        {
            (PluginComponent capped, int dropped) = Cap(node, limit);
            kept.Add(capped);
            trimmed += dropped;
        }

        return new()
        {
            Components = kept,
            WebView = view.WebView,
            Layout = view.Layout,
            RefreshInterval = view.RefreshInterval,
        };
    }

    /// <summary>The most children a component may carry, by name.</summary>
    private static int CapFor(string component) =>
        component switch
        {
            NmAppComponents.Grid => PluginPaging.MaximumPageSize,
            NmAppComponents.Carousel => PluginPaging.MaximumRowCards,
            NmAppComponents.Lolomo => PluginPaging.MaximumPageRows,
            _ => int.MaxValue,
        };

    private static (PluginComponent Node, int Dropped) Cap(PluginComponent node, int? limit)
    {
        Dictionary<string, object?> wire = node.Props;

        if (!wire.TryGetValue(ItemsKey, out object? raw) || raw is not IEnumerable<PluginComponent> children)
            return (node, 0);

        List<PluginComponent> all = [.. children];
        int cap = Math.Min(CapFor(node.Component), limit ?? int.MaxValue);
        int dropped = Math.Max(0, all.Count - cap);

        List<PluginComponent> kept = [];
        foreach (PluginComponent child in all.Take(cap))
        {
            (PluginComponent capped, int below) = Cap(child, null);
            kept.Add(capped);
            dropped += below;
        }

        // The children travel inside the props, so the new node carries the
        // same props with its kept children in place of the old list.
        wire[ItemsKey] = Array.Empty<PluginComponent>();

        return (
            new()
            {
                Id = node.Id,
                Component = node.Component,
                Props = wire,
                Items = kept,
            },
            dropped
        );
    }

    private static PluginComponent? Find(IEnumerable<PluginComponent> nodes, string id)
    {
        Stack<PluginComponent> pending = new(nodes.Reverse());

        while (pending.Count > 0)
        {
            PluginComponent node = pending.Pop();

            if (string.Equals(node.Id, id, StringComparison.Ordinal))
                return node;

            if (node.Props.TryGetValue(ItemsKey, out object? raw) && raw is IEnumerable<PluginComponent> children)
                foreach (PluginComponent child in children.Reverse())
                    pending.Push(child);
        }

        return null;
    }
}
