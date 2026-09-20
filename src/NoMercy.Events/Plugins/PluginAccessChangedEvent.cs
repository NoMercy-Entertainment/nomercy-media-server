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
/// Something that decides who may open a plugin has changed: an entitlement,
/// a revocation, a guest install being purged, or the owner's answer.
/// <para>
/// Says that the answer may be different, not what it now is. Who may open a
/// plugin is a different answer for every account, and working that out here
/// would be one of them deciding for all of them.
/// </para>
/// </summary>
public sealed class PluginAccessChangedEvent : EventBase
{
    public override string Source => "PluginAccess";

    /// <summary>
    /// The plugin whose answer may be different, or null when what changed
    /// was a list covering all of them: a revocation list arriving names the
    /// plugins it now blocks, not the ones it stopped blocking.
    /// </summary>
    public string? PluginId { get; init; }
}
