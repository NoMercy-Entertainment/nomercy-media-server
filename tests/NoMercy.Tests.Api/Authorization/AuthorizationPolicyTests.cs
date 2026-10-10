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
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Authorization;
using NoMercy.Database.Models.Users;
using Xunit;

namespace NoMercy.Tests.Api.Authorization;

[Trait("Category", "Authorization")]
public sealed class AuthorizationPolicyTests : IDisposable
{
    private static readonly Guid KnownUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UnknownUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public AuthorizationPolicyTests()
    {
        UserCache.Current.Reset();
        UserCache.Current.AddUser(
            new()
            {
                Id = KnownUserId,
                Email = "known@nomercy.tv",
                Name = "Known User",
                Owner = false,
                Manage = false,
                Allowed = true,
            }
        );
    }

    public void Dispose()
    {
        UserCache.Current.Reset();
    }

    private static IAuthorizationService BuildAuthorizationService()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services
            .AddAuthorizationBuilder()
            .AddPolicy(
                "api",
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim("scope", "openid", "profile");
                    policy.AddRequirements(
                        new AssertionRequirement(ctx =>
                        {
                            string? sub = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
                            if (!Guid.TryParse(sub, out Guid userId))
                                return false;
                            User? user = UserCache.Current.Users.FirstOrDefault(u =>
                                u.Id == userId
                            );
                            return user is not null;
                        })
                    );
                }
            );
        ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal BuildPrincipal(
        Guid userId,
        bool includeOpenid,
        bool includeProfile
    )
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];
        if (includeOpenid)
            claims.Add(new("scope", "openid"));
        if (includeProfile)
            claims.Add(new("scope", "profile"));

        ClaimsIdentity identity = new(claims, "TestScheme");
        return new(identity);
    }

    [Fact]
    public async Task ApiPolicy_Admits_WhenAuthenticatedWithScopesAndUserInCache()
    {
        IAuthorizationService authService = BuildAuthorizationService();
        ClaimsPrincipal principal = BuildPrincipal(
            KnownUserId,
            includeOpenid: true,
            includeProfile: true
        );

        AuthorizationResult result = await authService.AuthorizeAsync(principal, "api");

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ApiPolicy_Admits_WhenOnlyOpenidScopePresent()
    {
        IAuthorizationService authService = BuildAuthorizationService();
        ClaimsPrincipal principal = BuildPrincipal(
            KnownUserId,
            includeOpenid: true,
            includeProfile: false
        );

        AuthorizationResult result = await authService.AuthorizeAsync(principal, "api");

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ApiPolicy_Admits_WhenOnlyProfileScopePresent()
    {
        IAuthorizationService authService = BuildAuthorizationService();
        ClaimsPrincipal principal = BuildPrincipal(
            KnownUserId,
            includeOpenid: false,
            includeProfile: true
        );

        AuthorizationResult result = await authService.AuthorizeAsync(principal, "api");

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ApiPolicy_Denies_WhenNoScopeClaimPresent()
    {
        IAuthorizationService authService = BuildAuthorizationService();
        ClaimsPrincipal principal = BuildPrincipal(
            KnownUserId,
            includeOpenid: false,
            includeProfile: false
        );

        AuthorizationResult result = await authService.AuthorizeAsync(principal, "api");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ApiPolicy_Denies_WhenUserNotInCache()
    {
        IAuthorizationService authService = BuildAuthorizationService();
        ClaimsPrincipal principal = BuildPrincipal(
            UnknownUserId,
            includeOpenid: true,
            includeProfile: true
        );

        AuthorizationResult result = await authService.AuthorizeAsync(principal, "api");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ApiPolicy_Denies_WhenSubClaimMalformed()
    {
        IAuthorizationService authService = BuildAuthorizationService();

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, "not-a-guid"),
            new("scope", "openid"),
            new("scope", "profile"),
        ];
        ClaimsIdentity identity = new(claims, "TestScheme");
        ClaimsPrincipal principal = new(identity);

        AuthorizationResult result = await authService.AuthorizeAsync(principal, "api");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ApiPolicy_Denies_WhenUnauthenticated()
    {
        IAuthorizationService authService = BuildAuthorizationService();
        ClaimsPrincipal principal = new(new ClaimsIdentity());

        AuthorizationResult result = await authService.AuthorizeAsync(principal, "api");

        result.Succeeded.Should().BeFalse();
    }
}
