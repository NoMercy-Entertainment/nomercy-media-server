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

    /// <summary>
    /// The installed plugins that need the server's own process, by id, each
    /// with what it uses that a process of its own does not carry. Absent from
    /// the map means nothing in its manifest says it would lose anything. A
    /// plugin listed here and set to its own process is refused, not started.
    /// </summary>
    [JsonProperty("needs_in_process", NullValueHandling = NullValueHandling.Ignore)]
    public IReadOnlyDictionary<string, PluginInProcessNeedDto>? NeedsInProcess { get; init; }
}
