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

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NoMercy.Api.Middleware;

namespace NoMercy.Api.Filters;

/// <summary>The check behind <see cref="HmacSignedOrOwnerAttribute"/>.</summary>
internal sealed class HmacSignedOrOwnerFilter(
    IAuthorizationService authorizationService,
    IAuthorizationPolicyProvider policyProvider
) : IAsyncAuthorizationFilter
{
    private const string OwnerPolicyName = "Owner";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (HmacValidationMiddleware.IsVerified(context.HttpContext))
            return;

        AuthorizationPolicy defaultPolicy = await policyProvider.GetDefaultPolicyAsync();
        AuthorizationPolicy ownerPolicy =
            await policyProvider.GetPolicyAsync(OwnerPolicyName)
            ?? throw new InvalidOperationException(
                $"The '{OwnerPolicyName}' authorization policy is not registered."
            );

        ClaimsPrincipal user = context.HttpContext.User;
        AuthorizationResult result = await authorizationService.AuthorizeAsync(
            user,
            AuthorizationPolicy.Combine(defaultPolicy, ownerPolicy)
        );
        if (result.Succeeded)
            return;

        context.Result =
            user.Identity?.IsAuthenticated == true ? new ForbidResult() : new ChallengeResult();
    }
}
