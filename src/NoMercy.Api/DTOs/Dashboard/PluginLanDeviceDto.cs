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

namespace NoMercy.Api.DTOs.Dashboard;

/// <summary>
/// A device on the owner's network, as the dashboard lists it.
/// <para>
/// The credential is not here. It is shown once, when the device is added,
/// and after that the owner revokes and adds again rather than reading it
/// back out of a list.
/// </para>
/// </summary>
public record PluginLanDeviceDto
{
    [JsonProperty("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; init; } = string.Empty;

    [JsonProperty("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonProperty("revoked")]
    public bool Revoked { get; init; }
}

/// <summary>What the owner gets once, when they add a device.</summary>
public record PluginLanDeviceMintedDto
{
    [JsonProperty("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; init; } = string.Empty;

    [JsonProperty("credential")]
    public string Credential { get; init; } = string.Empty;

    /// <summary>The whole address to paste into the device. Relative: the client knows the host.</summary>
    [JsonProperty("url")]
    public string Url { get; init; } = string.Empty;
}

/// <summary>What the owner types when adding a device.</summary>
public record PluginLanDeviceRequestDto
{
    [JsonProperty("name")]
    public string Name { get; init; } = string.Empty;
}
