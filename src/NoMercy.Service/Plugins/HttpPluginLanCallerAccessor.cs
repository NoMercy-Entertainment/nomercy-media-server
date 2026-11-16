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

using NoMercy.PluginSdk.Lan;

namespace NoMercy.Service.Plugins;

/// <summary>
/// The LAN device the request being served was checked for, read from the
/// feature the LAN routes set. Null on every other request and outside one.
/// </summary>
public class HttpPluginLanCallerAccessor(IHttpContextAccessor accessor) : IPluginLanCallerAccessor
{
    public PluginLanDispatch? Current => accessor.HttpContext?.Features.Get<PluginLanDispatch>();
}
