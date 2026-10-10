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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Authorization;
using NoMercy.Service.Authorization;
using Xunit;

namespace NoMercy.Tests.Api.Authorization;

[Trait("Category", "Authorization")]
public sealed class MediaAuthorizationHandlerTests
{
    private static readonly Guid OwnerId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid ModeratorId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private static readonly Guid AllowedId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid StrangerId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static (MediaAuthorizationHandler handler, UserCache cache) BuildHandler()
    {
        UserCache cache = new();
        MediaAuthorizationPolicy policy = new(cache);
        MediaAuthorizationHandler handler = new(
            policy,
            NullLogger<MediaAuthorizationHandler>.Instance
        );
        return (handler, cache);
    }

    private static ClaimsPrincipal PrincipalFor(Guid userId)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];
        return new(new ClaimsIdentity(claims, "TestScheme"));
    }

    private static AuthorizationHandlerContext MakeContext(
        ClaimsPrincipal user,
        IAuthorizationRequirement requirement
    )
    {
        return new([requirement], user, null);
    }

    [Fact]
    public async Task OwnerRequirement_Succeeds_WhenPrincipalIsOwner()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(
            new()
            {
                Id = OwnerId,
                Owner = true,
                Name = "Owner",
                Email = "o@nm.tv",
            }
        );
        AuthorizationHandlerContext context = MakeContext(
            PrincipalFor(OwnerId),
            new OwnerRequirement()
        );

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task OwnerRequirement_Denies_WhenPrincipalIsNotOwner()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(
            new()
            {
                Id = OwnerId,
                Owner = true,
                Name = "Owner",
                Email = "o@nm.tv",
            }
        );
        AuthorizationHandlerContext context = MakeContext(
            PrincipalFor(StrangerId),
            new OwnerRequirement()
        );

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task ModeratorRequirement_Succeeds_WhenPrincipalHasManage()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(
            new()
            {
                Id = ModeratorId,
                Manage = true,
                Name = "Mod",
                Email = "m@nm.tv",
            }
        );
        AuthorizationHandlerContext context = MakeContext(
            PrincipalFor(ModeratorId),
            new ModeratorRequirement()
        );

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task ModeratorRequirement_Denies_WhenPrincipalLacksManageAndIsNotOwner()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(
            new()
            {
                Id = AllowedId,
                Allowed = true,
                Manage = false,
                Owner = false,
                Name = "Plain",
                Email = "p@nm.tv",
            }
        );
        AuthorizationHandlerContext context = MakeContext(
            PrincipalFor(AllowedId),
            new ModeratorRequirement()
        );

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task MediaAccessRequirement_Succeeds_WhenPrincipalHasAllowed()
    {
        (MediaAuthorizationHandler handler, UserCache cache) = BuildHandler();
        cache.AddUser(
            new()
            {
                Id = AllowedId,
                Allowed = true,
                Owner = false,
                Name = "Allowed",
                Email = "a@nm.tv",
            }
        );
        AuthorizationHandlerContext context = MakeContext(
            PrincipalFor(AllowedId),
            new MediaAccessRequirement()
        );

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task MediaAccessRequirement_Denies_WhenPrincipalNotInCache()
    {
        (MediaAuthorizationHandler handler, UserCache _) = BuildHandler();
        AuthorizationHandlerContext context = MakeContext(
            PrincipalFor(StrangerId),
            new MediaAccessRequirement()
        );

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Handler_DoesNotThrow_WhenPolicyThrows()
    {
        UserCache cache = new();
        ThrowingPolicy throwingPolicy = new();
        MediaAuthorizationHandler handler = new(
            throwingPolicy,
            NullLogger<MediaAuthorizationHandler>.Instance
        );

        cache.AddUser(
            new()
            {
                Id = OwnerId,
                Owner = true,
                Name = "Owner",
                Email = "o@nm.tv",
            }
        );

        AuthorizationHandlerContext context = MakeContext(
            PrincipalFor(OwnerId),
            new OwnerRequirement()
        );

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    private sealed class ThrowingPolicy : IMediaAuthorizationPolicy
    {
        public bool IsOwner(ClaimsPrincipal? principal) =>
            throw new InvalidOperationException("policy check exploded");

        public bool IsModerator(ClaimsPrincipal? principal) =>
            throw new InvalidOperationException("policy check exploded");

        public bool IsAllowed(ClaimsPrincipal? principal) =>
            throw new InvalidOperationException("policy check exploded");

        public bool IsOpticalAccess(ClaimsPrincipal? principal) =>
            throw new InvalidOperationException("policy check exploded");
    }
}
