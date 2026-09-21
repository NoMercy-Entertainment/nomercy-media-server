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
/// One page a plugin serves.
///
/// Declared rather than matched inside the view, because a route that only
/// exists as a case in a switch is one nothing else can see. A declared table
/// can be listed for the viewer, checked for two pages claiming the same path,
/// and asked which surfaces a page exists on before a client offers it.
/// </summary>
public class PluginRoute
{
    /// <summary>
    /// The path, relative to the plugin, with named parameters:
    /// `/stations/:id`, `/downloads`, `/`.
    /// </summary>
    public required string Path
    {
        get;
        init
        {
            if (PluginBasePath.IsReserved(value))
                throw new ArgumentException(
                    $"A plugin route cannot start with {PluginBasePath.Prefix}, which is reserved for the server's own pages. Rename it to something under the plugin, such as /overview.",
                    nameof(Path)
                );

            field = value;
        }
    }

    /// <summary>
    /// A stable name the plugin uses to link here, so a link survives the path
    /// being rewritten.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>What the viewer sees in a heading or a menu, as a translation key.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Who may open this page. A page that says nothing is shared: an author
    /// who forgets the word gets the safe half of the contract, not the owner's.
    /// </summary>
    public PluginRouteAccess Access { get; init; } = PluginRouteAccess.Shared;

    /// <summary>
    /// The shell this page sits in, from <see cref="PluginLayout" />. A page
    /// that says nothing gets the ordinary one.
    /// </summary>
    public string Layout { get; init; } = PluginLayout.Standard;

    /// <summary>
    /// A layout per surface, for the pages where one shell genuinely cannot
    /// serve every device. Falls back to <see cref="Layout" />.
    /// </summary>
    public Dictionary<string, string> LayoutBySurface { get; init; } = new();

    /// <summary>
    /// The surfaces this page exists on. Empty means all of them: an author who
    /// says nothing wants their page everywhere, not nowhere.
    /// </summary>
    public List<string> Surfaces { get; init; } = [];

    /// <summary>Whether this page exists on the given surface.</summary>
    public bool ExistsOn(string surface)
    {
        return Surfaces.Count == 0 || Surfaces.Contains(surface);
    }

    /// <summary>The shell for one surface.</summary>
    public string LayoutFor(string surface)
    {
        return LayoutBySurface.TryGetValue(surface, out string? layout) ? layout : Layout;
    }

    /// <summary>
    /// Whether [path] is this route, and what its parameters were.
    ///
    /// Returns null rather than an empty dictionary on a miss, so "matched with
    /// no parameters" and "did not match" cannot be confused.
    /// </summary>
    public Dictionary<string, string>? Match(string path)
    {
        string[] wanted = Segments(Path);
        string[] given = Segments(path);

        if (wanted.Length != given.Length)
            return null;

        Dictionary<string, string> parameters = new();

        for (int index = 0; index < wanted.Length; index++)
        {
            string part = wanted[index];

            if (part.StartsWith(':'))
            {
                parameters[part[1..]] = Uri.UnescapeDataString(given[index]);
                continue;
            }

            if (!string.Equals(part, given[index], StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return parameters;
    }

    /// <summary>
    /// The path for this route with [parameters] filled in, which is how a
    /// plugin links to its own pages without writing a path twice.
    /// </summary>
    public string Build(IReadOnlyDictionary<string, string>? parameters = null)
    {
        IEnumerable<string> parts = Segments(Path)
            .Select(part =>
            {
                if (!part.StartsWith(':'))
                    return part;

                string key = part[1..];

                if (parameters is null || !parameters.TryGetValue(key, out string? value))
                    throw new ArgumentException(
                        $"route '{Name}' needs a '{key}'",
                        nameof(parameters)
                    );

                return Uri.EscapeDataString(value);
            });

        string built = string.Join('/', parts);

        return built.Length == 0 ? "/" : $"/{built}";
    }

    private static string[] Segments(string path)
    {
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>
/// The shells a page can sit in.
///
/// A layout is a shape rather than a design: the client draws it in its own
/// idiom, which is what lets one declaration look right with a pointer and with
/// a remote. A plugin choosing pixels would be choosing them for a screen it
/// cannot see.
/// </summary>
public static class PluginLayout
{
    /// <summary>Content in the section's ordinary chrome.</summary>
    public const string Standard = "standard";

    /// <summary>A list beside the thing it selects, collapsing to two pages where there is no room.</summary>
    public const string ListDetail = "list-detail";

    /// <summary>Tiles, for things that are mostly artwork.</summary>
    public const string Grid = "grid";

    /// <summary>One thing, with the chrome out of the way.</summary>
    public const string Immersive = "immersive";

    /// <summary>A form, which is a shape a remote control handles badly.</summary>
    public const string Form = "form";

    /// <summary>
    /// The width the page is given, with the page's ordinary padding.
    /// <para>
    /// <see cref="Standard" /> holds a page to a measure a person can read
    /// across, which is right for a page of cards and wrong for a page of data:
    /// a table wants every column it declared. There was no shape meaning this,
    /// so a nine-column table lost five columns and its row buttons.
    /// </para>
    /// </summary>
    public const string Wide = "wide";

    public static readonly string[] All = [Standard, ListDetail, Grid, Immersive, Form, Wide];

    public static bool IsKnown(string? layout)
    {
        return layout is not null && Array.IndexOf(All, layout) >= 0;
    }
}
