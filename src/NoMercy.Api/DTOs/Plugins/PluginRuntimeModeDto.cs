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
using NoMercy.PluginSdk.OutOfProcess;

namespace NoMercy.Api.DTOs.Plugins;

/// <summary>
/// Where this server runs its plugins, as the dashboard reads and writes it.
/// </summary>
public record PluginRuntimeModeDto
{
    [JsonProperty("isolation")]
    public string Isolation { get; init; } = nameof(PluginIsolation.InProcess);

    /// <summary>
    /// The plugins that answer differently from the rest. Trying the new
    /// runtime on one plugin somebody can afford to have stop is a different
    /// decision from moving all of them, so it is a different field.
    /// </summary>
    [JsonProperty("per_plugin")]
    public IReadOnlyDictionary<string, PluginIsolation> PerPlugin { get; init; } =
        new Dictionary<string, PluginIsolation>();

    /// <summary>
    /// A key the clients translate, present only when this server cannot yet
    /// do what was asked. A sentence here would be one the phone and the
    /// television could not say in the owner's own language.
    /// </summary>
    [JsonProperty("notice", NullValueHandling = NullValueHandling.Ignore)]
    public string? Notice { get; init; }
}
