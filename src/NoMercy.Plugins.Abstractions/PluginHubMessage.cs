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

using System.Text.Json.Nodes;

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Something a client sent to a plugin over the hub.
/// <para>
/// <see cref="Payload"/> is a <see cref="JsonNode"/> and not a
/// <c>Newtonsoft.Json.Linq.JToken</c>. Newtonsoft is a package, so it is copied
/// into a plugin's output and takes a distinct identity in the plugin's load
/// context: a plugin handed a <c>JToken</c> could not bind to it. <c>JsonNode</c>
/// ships with the shared framework, so it resolves from the default context
/// with no allowlist entry at all.
/// </para>
/// <para>The wire envelope is unaffected; only the managed type changes.</para>
/// </summary>
public class PluginHubMessage
{
    public required string Method { get; init; }
    public JsonNode? Payload { get; init; }
    public required string ConnectionId { get; init; }
    public string? UserId { get; init; }

    /// <summary>
    /// Who sent it, as the host resolved them. Null when the host could not
    /// resolve a caller, and a handler registered through
    /// <see cref="IPluginHubContext.Handle" /> is not run in that case: a hub
    /// method that cannot tell the owner from a guest ends up trusting whoever
    /// connected.
    /// </summary>
    public PluginCaller? Caller { get; init; }
}
