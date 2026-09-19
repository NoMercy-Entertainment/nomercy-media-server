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

using System.Text.Json.Serialization;

namespace NoMercy.Plugins.Abstractions;

/// <summary>Which of a plugin's routes a client is asking to render.</summary>
public class PluginViewRequest
{
    [JsonPropertyName("route")]
    public required string Route { get; init; }

    [JsonPropertyName("query")]
    public Dictionary<string, string> Query { get; init; } = new();

    /// <summary>
    /// Who is asking, with the role and access the host resolved. A view is
    /// served on the caller's behalf, not the server's.
    /// </summary>
    [JsonPropertyName("caller")]
    public required PluginCaller Caller { get; init; }

    /// <summary>The caller's id, so a plugin that only wants that keeps reading it.</summary>
    [JsonPropertyName("userId")]
    public UserId UserId => Caller.Id;

    /// <summary>
    /// Which kind of screen is asking, from <see cref="PluginSurface" />.
    ///
    /// A television is not a narrow desktop. Some views differ by a hidden
    /// column, which a component handles itself through its box, and some are a
    /// different page entirely — a grid of posters on a TV where the desktop
    /// shows a table. A plugin that cannot tell them apart has to pick one and
    /// be wrong on the other two.
    ///
    /// Defaults to the roomiest surface, so a caller that says nothing gets the
    /// view with the most in it rather than the most stripped down.
    /// </summary>
    [JsonPropertyName("surface")]
    public string Surface { get; init; } = PluginSurface.Web;
}
