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

using Newtonsoft.Json;

namespace NoMercy.Api.Plugins;

/// <summary>
/// A refusal on the wire, in the shape design section 3.9 fixes: one code, one
/// sentence each for what happened, why it was refused and how to fix it.
/// <para>
/// A bare status code tells a publisher their plugin stopped working and
/// nothing else. This tells them which line to change.
/// </para>
/// </summary>
public class PluginRefusalDto
{
    [JsonProperty("code")]
    public required string Code { get; init; }

    [JsonProperty("plugin")]
    public required string Plugin { get; init; }

    [JsonProperty("what")]
    public required string What { get; init; }

    [JsonProperty("why")]
    public required string Why { get; init; }

    [JsonProperty("fix")]
    public required string Fix { get; init; }

    /// <summary>
    /// <c>blocked</c> (the call did not happen), <c>degraded</c> (a fallback
    /// ran) or <c>warning</c> (allowed this release, blocked next major).
    /// </summary>
    [JsonProperty("severity")]
    public string Severity { get; init; } = "blocked";
}
