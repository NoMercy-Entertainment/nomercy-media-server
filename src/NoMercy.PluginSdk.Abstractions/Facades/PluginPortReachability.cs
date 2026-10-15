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
/// What a probe of the server's public address found for one port.
/// <para>
/// Three answers, not two: a plugin that shows the owner a warning must not
/// show one when the server simply could not tell. Only <see cref="Closed" />
/// is a reason to warn.
/// </para>
/// </summary>
public enum PluginPortReachability
{
    /// <summary>
    /// The server could not tell: UDP, which a connection cannot test; no
    /// router that answered with the public address; or a host that does not
    /// probe.
    /// </summary>
    Unknown,

    /// <summary>A connection to the public address and this port was accepted.</summary>
    Open,

    /// <summary>A connection to the public address and this port was refused or timed out.</summary>
    Closed,
}
