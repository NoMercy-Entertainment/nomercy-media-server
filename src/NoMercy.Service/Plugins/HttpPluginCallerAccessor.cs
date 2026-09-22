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

using Microsoft.AspNetCore.Http;
using NoMercy.Authorization;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Service.Plugins;

/// <summary>
/// Who is asking, read from the request being served.
/// <para>
/// Empty outside one, which is the honest answer for a scheduled job or a
/// startup hook: nobody asked, so there is no account to mint anything for.
/// </para>
/// </summary>
public class HttpPluginCallerAccessor(IHttpContextAccessor accessor) : IPluginCallerAccessor
{
    public Guid CurrentUserId => accessor.HttpContext?.User.UserId() ?? Guid.Empty;
}
