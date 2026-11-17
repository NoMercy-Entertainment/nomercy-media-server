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

namespace NoMercy.Design;

/// <summary>
/// The components a plugin may never name, at any depth of its view.
///
/// <para>
/// <c>NMServerComponent</c> loads a script from the server and runs it in the
/// viewer's browser. The app's own screens use it; a plugin must not, because
/// that would run code the owner never reviewed under every viewer's session.
/// </para>
/// <para>
/// A list of what is refused rather than of what is allowed. A name a client
/// does not know already draws a short "cannot show" note in its place, and the
/// rest of the page still works. A server-side allow-list would turn that note
/// into a refused page, and it would have to repeat the web and the Compose
/// vocabularies here, where any drift refuses a page that works today. The
/// clients keep their own allow-lists; this is the layer on top of them.
/// </para>
/// <para>
/// Add a component here when it loads remote code or raw HTML into a client.
/// </para>
/// </summary>
public static class NmPluginForbiddenComponents
{
    /// <summary>Loads and runs a script from a server path in the viewer's browser.</summary>
    public const string ServerComponent = "NMServerComponent";

    /// <summary>
    /// Every forbidden name. Compared without case, because a client that ever
    /// matched without case would otherwise let a respelled name through.
    /// </summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ServerComponent };

    /// <summary>
    /// Whether a plugin view naming <paramref name="component" /> must be
    /// refused. Spaces around the name are ignored, as is case, so a padded or
    /// respelled name is refused too.
    /// </summary>
    public static bool IsForbidden(string? component) =>
        component is not null && All.Contains(component.Trim());
}
