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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Any component the design system publishes, as a node a view can hold.
///
/// <para>
/// <see cref="PluginViews"/> covers the shapes a plugin reaches for constantly —
/// a list of cards, a table, a form — and each is a composition of a handful of
/// components. This is the other half: the fifty-six components themselves, each
/// with its own props record, so a plugin that wants an accordion or a stepper
/// writes one rather than discovering that the platform only ever offered it ten
/// of them.
/// </para>
/// </summary>
public static class PluginDesign
{
    /// <summary>
    /// A node drawing whichever component these props belong to.
    ///
    /// <para>
    /// The record states which component it belongs to, so the pairing cannot be
    /// wrong. Passing the name separately would let a caller hand
    /// <c>NMButton</c> a card's props, which renders as a button with nothing on
    /// it and no error anywhere.
    /// </para>
    /// </summary>
    public static PluginComponent Node(string id, NmProps props) =>
        new()
        {
            Id = id,
            Component = ComponentOf(props),
            Design = props,
        };

    /// <inheritdoc cref="Node(string, NmProps)"/>
    public static PluginComponent Node(string id, NmProps props, PluginActionIntent action) =>
        new()
        {
            Id = id,
            Component = ComponentOf(props),
            Design = props,
            Action = action,
        };

    /// <summary>The component a props record belongs to, as the record states it.</summary>
    public static string ComponentOf(NmProps props) => props.Component;
}
