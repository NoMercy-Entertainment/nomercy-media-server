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
/// Every component tag a client is expected to render.
/// <para>
/// The tag is a string on the wire, which means a typo produces a node that
/// resolves to nothing in the web map and fails to deserialise in the Kotlin
/// sealed set — in neither case as an error anyone sees. Naming them here makes
/// the typo a compile error instead.
/// </para>
/// <para>
/// Adding a tag means adding it in three places in the same change: here, the
/// web component map, and the Kotlin sealed set. A tag one client knows and
/// another does not renders as a hole.
/// </para>
/// </summary>
public static class PluginComponentType
{
    public const string Container = "NMCard";
    public const string Text = "NMText";
    public const string Image = "NMImage";
    public const string List = "NMCard";
    public const string Row = "NMCard";
    public const string Grid = "NMCard";
    public const string Card = "NMCard";
    public const string Detail = "NMCard";
    public const string Button = "NMButton";

    /// <summary>
    /// A real form: it collects what its controls hold and puts that in the
    /// action's payload.
    /// <para>
    /// The one tag here that cannot be a design-system component. NMCard draws
    /// the fields and a submit button perfectly well, and carries the action the
    /// server wrote at build time - so a person typed a full indexer into a
    /// settings page, pressed Save, and the plugin's config was written with the
    /// name set and every other value empty, while the page reported success. No
    /// plugin's settings could be filled in through the dashboard at all.
    /// </para>
    /// <para>
    /// The fields inside it stay design-system components, so it looks like the
    /// rest of the interface. Only the collecting is this component's own.
    /// </para>
    /// </summary>
    public const string Form = "PluginForm";
    public const string WebView = "PluginWebView";

    /// <summary>
    /// A piece of media the plugin serves, played by the client's own player.
    ///
    /// <para>
    /// The host's, not the plugin's, for the same reason the form is: a card
    /// that played its own stream got a second, smaller player with no artist,
    /// no cover and none of the remote and cast handling the real one has.
    /// What it carries is a <see cref="PluginMediaRef" />, whose url the host
    /// minted.
    /// </para>
    /// </summary>
    public const string MediaCard = "PluginMediaCard";
    public const string EmptyState = "NMEmptyState";
    public const string Spinner = "NMSpinner";

    /// <summary>
    /// Columns plus rows, each row optionally carrying an action.
    /// <para>
    /// The one tag still drawn by the pre-design-system map. NMTable is a bare
    /// table element with a single slot, and the design system generates no row
    /// or cell component to put in it, so a payload cannot build one. Every
    /// other tag here now names an NM component; this one changes the day those
    /// two exist.
    /// </para>
    /// </summary>
    public const string Table = "NMCard";

    /// <summary>Determinate when a value is set, indeterminate when it is not.</summary>
    public const string Progress = "NMProgress";

    /// <summary>A status chip carrying a semantic variant, never a colour.</summary>
    public const string Badge = "NMBadge";

    /// <summary>The tags every client is expected to render.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Container,
            Text,
            Image,
            List,
            Row,
            Grid,
            Card,
            Detail,
            Button,
            Form,
            WebView,
            MediaCard,
            EmptyState,
            Spinner,
            Table,
            Progress,
            Badge,
        };
}
