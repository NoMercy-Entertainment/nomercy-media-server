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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// What a plugin puts on screen for one route: either a declarative tree the
/// client renders natively, or a webview it does not.
/// <para>
/// Exactly one of the two is set. The webview is the escape hatch and is meant
/// to stay one — a table with progress bars is ordinary UI, and
/// <see cref="PluginComponentType"/> can express it.
/// </para>
/// </summary>
public class PluginView
{
    [JsonPropertyName("components")]
    public List<PluginComponent>? Components { get; init; }

    [JsonPropertyName("webView")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginWebViewRef? WebView { get; init; }

    /// <inheritdoc cref="PluginActionIntent.ShouldSerializeConfirm" />
    public bool ShouldSerializeWebView() => WebView is not null;

    /// <summary>
    /// How often the client should re-fetch this route, in seconds. Zero means
    /// never — a view that changes on its own says so here instead of every
    /// client guessing a poll interval, and a plugin with a hub connection
    /// pushes instead.
    /// </summary>
    /// <summary>
    /// The shell this view sits in, from <see cref="PluginLayout" />.
    ///
    /// A shape rather than a design: the client draws it in its own idiom, which
    /// is what lets one declaration look right with a pointer and with a remote.
    /// </summary>
    [JsonPropertyName("layout")]
    public string Layout { get; set; } = PluginLayout.Standard;

    [JsonPropertyName("refreshInterval")]
    public int RefreshInterval { get; init; }
}
