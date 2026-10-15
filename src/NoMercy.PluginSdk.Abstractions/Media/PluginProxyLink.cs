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
/// One upstream for a stream, with the headers that upstream wants.
/// Links are tried in order, so a provider that drops a connection falls
/// through to the next one instead of ending the viewer's playback.
/// </summary>
public sealed record PluginProxyLink
{
    public required Uri Url { get; init; }
    public string? Referer { get; init; }

    /// <summary>
    /// The user agent to fetch this link with, as the viewer's player would
    /// have sent it. Providers that allow-list on a player or set-top-box name
    /// need it. It applies to media fetches only: the server still sends its
    /// own identity and the plugin's name, in the <c>X-NoMercy-Attribution</c>
    /// header. A value with control characters is ignored. Children of a
    /// rewritten playlist carry the user agent of the link that answered.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Resolves a credential-bearing upstream just before the fetch. It runs on
    /// the server and its answer never reaches a client, which is why a plugin
    /// can hold a provider password without a viewer ever seeing one.
    /// </summary>
    public Func<CancellationToken, Task<Uri>>? ResolveAsync { get; init; }
}
