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
/// The owner's answer for one capability.
/// <para>
/// Sent as a list so a page of answers is one decision. Sent one at a time, an
/// owner who approved four and refused one would have made five decisions and
/// a failure halfway leaves a state nobody chose.
/// </para>
/// </summary>
public class PluginCapabilityDecisionDto
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("approved")]
    public bool Approved { get; set; }
}
