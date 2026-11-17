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

using System.Collections;
using Newtonsoft.Json.Linq;
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
/// A node's children can arrive in three shapes, and all three are read: a
/// <see cref="PluginComponent" />, a JSON object (what a <c>Design</c> record
/// turns into inside the props), or a dictionary with string keys written by
/// hand (<c>id</c>, <c>component</c>, <c>props.items</c>). Keys are matched
/// without case. Any other object in a list still counts against the cap, but
/// is not looked into and cannot be the target of an append.
/// </para>
/// <para>
/// It builds a new view rather than changing the plugin's: a plugin that keeps
/// one tree and returns it twice must not find it trimmed the second time.
/// JSON objects are cloned and dictionaries copied before anything changes.
/// </para>
/// </summary>
public static class PluginShelfCaps
{
    private const string ItemsKey = "items";
    private const string PropsKey = "props";
    private const string IdKey = "id";
    private const string ComponentKey = "component";

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
                ? [(AsComponent(shelf), page.Limit)]
                : []
            : [.. view.Components.Select(node => (node, (int?)null))];

        List<PluginComponent> kept = [];
        foreach ((PluginComponent node, int? limit) in roots)
        {
            (object capped, int dropped) = CapNode(node, limit);
            kept.Add((PluginComponent)capped);
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
    private static int CapFor(string? component) =>
        component switch
        {
            NmAppComponents.Grid => PluginPaging.MaximumPageSize,
            NmAppComponents.Carousel => PluginPaging.MaximumRowCards,
            NmAppComponents.Lolomo => PluginPaging.MaximumPageRows,
            _ => int.MaxValue,
        };

    private static int CapFor(object node, int? limit) =>
        Math.Min(CapFor(ComponentOf(node)), limit ?? int.MaxValue);

    /// <summary>One node with its children held to the cap, in a copy when anything is held.</summary>
    private static (object Node, int Dropped) CapNode(object node, int? limit)
    {
        switch (node)
        {
            case PluginComponent component:
            {
                Dictionary<string, object?> wire = component.Props;
                if (!wire.TryGetValue(ItemsKey, out object? raw) || !IsList(raw))
                    return (component, 0);

                (object items, int dropped) = CapItems(raw!, CapFor(component, limit));

                // The children travel inside the props, so the new node carries
                // the same props with its kept children in place of the old list.
                List<PluginComponent>? typed =
                    items is List<object?> list && list.All(child => child is PluginComponent)
                        ? [.. list.Cast<PluginComponent>()]
                        : null;
                wire[ItemsKey] = typed is null ? items : Array.Empty<PluginComponent>();

                return (
                    new PluginComponent
                    {
                        Id = component.Id,
                        Component = component.Component,
                        Props = wire,
                        Items = typed ?? [],
                    },
                    dropped
                );
            }

            case JObject json:
            {
                if (
                    Property(json, PropsKey) is not JObject props
                    || Property(props, ItemsKey) is not JArray
                )
                    return (json, 0);

                JObject copy = (JObject)json.DeepClone();
                JObject copyProps = (JObject)Property(copy, PropsKey)!;
                JProperty items = copyProps
                    .Properties()
                    .First(property => Is(property.Name, ItemsKey));

                (object capped, int dropped) = CapItems(items.Value, CapFor(json, limit));
                items.Value = (JArray)capped;

                return (copy, dropped);
            }

            case IDictionary dictionary:
            {
                Dictionary<string, object?> copy = Copy(dictionary);
                if (Entry(copy, PropsKey) is not (string propsKey, IDictionary props))
                    return (dictionary, 0);

                Dictionary<string, object?> copyProps = Copy(props);
                if (Entry(copyProps, ItemsKey) is not (string itemsKey, object raw) || !IsList(raw))
                    return (dictionary, 0);

                (object capped, int dropped) = CapItems(raw, CapFor(dictionary, limit));
                copyProps[itemsKey] = capped;
                copy[propsKey] = copyProps;

                return (copy, dropped);
            }

            default:
                return (node, 0);
        }
    }

    /// <summary>
    /// The first <paramref name="cap" /> children, each held to its own cap, in
    /// a new list. A JSON array stays a JSON array; anything else becomes a list.
    /// </summary>
    private static (object Items, int Dropped) CapItems(object raw, int cap)
    {
        if (raw is JArray array)
        {
            JArray kept = [];
            int dropped = Math.Max(0, array.Count - cap);

            foreach (JToken child in array.Take(cap))
            {
                (object capped, int below) = CapNode(child, null);
                // A token that still has a parent is cloned as it is added, so
                // the plugin's own array is never re-parented.
                kept.Add((JToken)capped);
                dropped += below;
            }

            return (kept, dropped);
        }

        List<object?> all = [.. ((IEnumerable)raw).Cast<object?>()];
        List<object?> keptItems = [];
        int droppedItems = Math.Max(0, all.Count - cap);

        foreach (object? child in all.Take(cap))
        {
            if (child is null)
            {
                keptItems.Add(null);
                continue;
            }

            (object capped, int below) = CapNode(child, null);
            keptItems.Add(capped);
            droppedItems += below;
        }

        return (keptItems, droppedItems);
    }

    /// <summary>The node with this id, in any of the three shapes, depth first in view order.</summary>
    private static object? Find(IEnumerable<PluginComponent> roots, string id)
    {
        Stack<object> pending = new(roots.Reverse());

        while (pending.Count > 0)
        {
            object node = pending.Pop();

            if (string.Equals(IdOf(node), id, StringComparison.Ordinal))
                return node;

            foreach (object child in ChildrenOf(node).Reverse())
                pending.Push(child);
        }

        return null;
    }

    /// <summary>
    /// A found target as a node the view can hold. A JSON object or a
    /// dictionary keeps its props, read into a new dictionary.
    /// </summary>
    private static PluginComponent AsComponent(object node)
    {
        if (node is PluginComponent component)
            return component;

        Dictionary<string, object?> props = node switch
        {
            JObject json when Property(json, PropsKey) is JObject jsonProps => jsonProps
                .Properties()
                .ToDictionary(
                    property => property.Name,
                    property => (object?)property.Value.DeepClone()
                ),
            IDictionary dictionary
                when Entry(Copy(dictionary), PropsKey) is (_, IDictionary dictProps) => Copy(
                dictProps
            ),
            _ => new(),
        };

        return new()
        {
            Id = IdOf(node) ?? string.Empty,
            Component = ComponentOf(node) ?? string.Empty,
            Props = props,
        };
    }

    private static IEnumerable<object> ChildrenOf(object node)
    {
        object? items = node switch
        {
            PluginComponent component => component.Props.GetValueOrDefault(ItemsKey),
            JObject json => Property(json, PropsKey) is JObject props
                ? Property(props, ItemsKey)
                : null,
            IDictionary dictionary => Entry(Copy(dictionary), PropsKey) is (_, IDictionary props)
                ? Entry(Copy(props), ItemsKey)?.Value
                : null,
            _ => null,
        };

        if (!IsList(items))
            return [];

        return ((IEnumerable)items!).Cast<object?>().OfType<object>();
    }

    private static string? IdOf(object node) =>
        node switch
        {
            PluginComponent component => component.Id,
            JObject json => (Property(json, IdKey) as JValue)?.Value?.ToString(),
            IDictionary dictionary => Entry(Copy(dictionary), IdKey)?.Value?.ToString(),
            _ => null,
        };

    private static string? ComponentOf(object node) =>
        node switch
        {
            PluginComponent component => component.Component,
            JObject json => (Property(json, ComponentKey) as JValue)?.Value?.ToString(),
            IDictionary dictionary => Entry(Copy(dictionary), ComponentKey)?.Value?.ToString(),
            _ => null,
        };

    /// <summary>A list of children: a JSON array or any sequence that is not text or a dictionary.</summary>
    private static bool IsList(object? value) =>
        value is JArray || value is IEnumerable and not string and not JToken and not IDictionary;

    private static JToken? Property(JObject json, string name) =>
        json.Properties().FirstOrDefault(property => Is(property.Name, name))?.Value;

    private static (string Key, object? Value)? Entry(
        Dictionary<string, object?> dictionary,
        string name
    )
    {
        foreach (KeyValuePair<string, object?> entry in dictionary)
            if (Is(entry.Key, name))
                return (entry.Key, entry.Value);

        return null;
    }

    private static Dictionary<string, object?> Copy(IDictionary dictionary)
    {
        Dictionary<string, object?> copy = new();
        foreach (DictionaryEntry entry in dictionary)
            copy[entry.Key.ToString() ?? string.Empty] = entry.Value;

        return copy;
    }

    private static bool Is(string key, string name) =>
        string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
}
