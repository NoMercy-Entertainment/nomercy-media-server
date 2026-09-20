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

namespace NoMercy.Events.Plugins;

/// <summary>
/// A plugin needs the owner to look at its dashboard entry again before it can
/// run — most commonly because its manifest now asks for more than the owner
/// already approved.
/// </summary>
public sealed class PluginConsentRequiredEvent : EventBase
{
    public override string Source => "PluginManager";

    public required string PluginId { get; init; }
    public required string PluginName { get; init; }
    public required string Reason { get; init; }
}
