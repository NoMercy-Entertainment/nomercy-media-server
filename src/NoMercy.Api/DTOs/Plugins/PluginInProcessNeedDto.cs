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

namespace NoMercy.Api.DTOs.Plugins;

/// <summary>
/// Why one plugin needs the server's own process: what it uses that a process
/// of its own does not carry. Names rather than a sentence, so every client
/// says it in the owner's language.
/// </summary>
public record PluginInProcessNeedDto
{
    /// <summary>Plugin context facades it declares and the other process leaves empty.</summary>
    [JsonProperty("facades")]
    public IReadOnlyList<string> Facades { get; init; } = [];

    /// <summary>Entry-point hooks it declares that the server would not call there.</summary>
    [JsonProperty("hooks")]
    public IReadOnlyList<string> Hooks { get; init; } = [];

    /// <summary>Whether it serves REST controllers, which have no route there.</summary>
    [JsonProperty("controllers")]
    public bool Controllers { get; init; }
}
