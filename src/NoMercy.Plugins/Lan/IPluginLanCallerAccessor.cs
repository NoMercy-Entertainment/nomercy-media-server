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

namespace NoMercy.PluginSdk.Lan;

/// <summary>
/// Which LAN device is asking, right now, as the server checked it.
/// <para>
/// The device-side twin of <c>IPluginCallerAccessor</c>, and a server type
/// for the same reason <see cref="PluginLanDispatch" /> is: a media link is
/// bound to whoever this says is asking, so only the server may say it.
/// Null outside a LAN request.
/// </para>
/// </summary>
public interface IPluginLanCallerAccessor
{
    PluginLanDispatch? Current { get; }
}
