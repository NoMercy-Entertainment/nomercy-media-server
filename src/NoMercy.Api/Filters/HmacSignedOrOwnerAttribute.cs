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

using Microsoft.AspNetCore.Mvc;

namespace NoMercy.Api.Filters;

/// <summary>
/// Lets a remote encoder worker call an action with its request signature as the
/// only credential, and keeps every other caller to the server owner.
///
/// A request that HmacValidationMiddleware verified passes. Any other request
/// must satisfy the default policy and the Owner policy, the same checks a plain
/// <c>[Authorize]</c> plus <c>[Authorize(Policy = "Owner")]</c> apply.
///
/// The action must also carry <c>[AllowAnonymous]</c>: a headless worker has no
/// bearer token, and without it UseAuthorization and AccessLogMiddleware turn the
/// worker away before the signature is checked. This filter is the gate that
/// replaces them, so never use <c>[AllowAnonymous]</c> on these actions without it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HmacSignedOrOwnerAttribute()
    : TypeFilterAttribute(typeof(HmacSignedOrOwnerFilter));
