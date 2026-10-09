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
using Microsoft.AspNetCore.Authorization;
using NoMercy.Authorization;

namespace NoMercy.Service.Authorization;

/// <summary>
/// Bridges ASP.NET Core policy evaluation to <see cref="IMediaAuthorizationPolicy"/>,
/// the single source of truth for owner / moderator / media-access decisions. Lets
/// endpoints declare intent with <c>[Authorize(Policy = ...)]</c> instead of repeating
/// imperative permission checks, while reusing the exact same rule engine.
/// </summary>
public class MediaAuthorizationHandler(
    IMediaAuthorizationPolicy policy,
    ILogger<MediaAuthorizationHandler> logger
) : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (IAuthorizationRequirement requirement in context.Requirements)
        {
            // A policy-check throw otherwise surfaces as an unlogged 500; log and
            // leave the requirement unmet (deny) instead.
            try
            {
                switch (requirement)
                {
                    case OwnerRequirement when policy.IsOwner(context.User):
                        context.Succeed(requirement);
                        break;
                    case ModeratorRequirement when policy.IsModerator(context.User):
                        context.Succeed(requirement);
                        break;
                    case MediaAccessRequirement when policy.IsAllowed(context.User):
                        context.Succeed(requirement);
                        break;
                    case OpticalAccessRequirement when policy.IsOpticalAccess(context.User):
                        context.Succeed(requirement);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Authorization check {Requirement} threw — denying",
                    requirement.GetType().Name
                );
            }
        }

        return Task.CompletedTask;
    }
}
