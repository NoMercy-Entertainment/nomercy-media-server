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
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace NoMercy.Tests.Api.Infrastructure;

public static class TestAuthDefaults
{
    public const string AuthenticationScheme = "TestScheme";
    public const string TestAuthHeader = "X-Test-Auth";
    public const string Deny = "deny";

    // Lets a request impersonate a second, distinct identity (see
    // TestAuthHandler.SecondaryUserId) for ownership-isolation tests, without
    // giving every other test — which never sends this header — any different
    // behavior than before.
    public const string TestUserIdHeader = "X-Test-User-Id";
}

public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public static Guid DefaultUserId { get; } = Guid.Parse("37d03e60-7b0a-4246-a85b-a5618966a383");
    public static string DefaultUserName { get; } = "Test User";
    public static string DefaultUserEmail { get; } = "test@nomercy.tv";

    // A second, distinct seeded user (see NoMercyApiFactory.SeedMediaData) so
    // ownership-isolation tests (e.g. "user B gets 404 on user A's playlist")
    // can impersonate a real, allowed-but-unrelated identity via
    // TestAuthDefaults.TestUserIdHeader instead of the single fixed identity
    // every other controller test in this fixture relies on.
    public static Guid SecondaryUserId { get; } =
        Guid.Parse("8f2c1a90-4b3d-4e7a-9c1f-6d2e8a5b3c71");
    public static string SecondaryUserName { get; } = "Secondary Test User";
    public static string SecondaryUserEmail { get; } = "test-secondary@nomercy.tv";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (
            Request.Headers.TryGetValue(TestAuthDefaults.TestAuthHeader, out StringValues value)
            && value.ToString() == TestAuthDefaults.Deny
        )
        {
            return Task.FromResult(AuthenticateResult.Fail("Authentication denied by test"));
        }

        Guid userId = DefaultUserId;
        string userName = DefaultUserName;
        string userEmail = DefaultUserEmail;

        if (
            Request.Headers.TryGetValue(
                TestAuthDefaults.TestUserIdHeader,
                out StringValues userIdHeader
            )
            && userIdHeader.ToString() == SecondaryUserId.ToString()
        )
        {
            userId = SecondaryUserId;
            userName = SecondaryUserName;
            userEmail = SecondaryUserEmail;
        }
        else if (
            userIdHeader.ToString() is { Length: > 0 } rawUserId
            && Guid.TryParse(rawUserId, out Guid impersonatedUserId)
        )
        {
            // Any other well-formed id is an authenticated identity the server has
            // never registered — what a valid Keycloak token for a stranger looks
            // like — so permission-policy tests can prove the deny path.
            userId = impersonatedUserId;
            userName = "Unregistered Test User";
            userEmail = "unregistered@nomercy.tv";
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.Email, userEmail),
            new(ClaimTypes.Role, "user"),
            // Single space-delimited scope claim, mirroring the real Keycloak token
            // shape so tests exercise the same split-aware ApiScopePolicy path as
            // production rather than a pre-split multi-claim shape that masks it.
            new("scope", "openid profile email"),
        ];

        ClaimsIdentity identity = new(claims, TestAuthDefaults.AuthenticationScheme);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, TestAuthDefaults.AuthenticationScheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Genuinely anonymous test scheme — unlike <see cref="TestAuthHandler"/>, which
/// authenticates every request by default (a request must opt OUT via
/// <see cref="TestAuthDefaults.Deny"/>), this handler never produces a principal.
/// A request through this scheme carries no bearer token and no ambient identity,
/// matching what a real anonymous machine caller (the intake webhook, a
/// HMAC-authenticated worker) actually sends. Used by
/// <see cref="AnonymousNoMercyApiFactory"/> to exercise the real anonymous
/// pipeline — AccessLogMiddleware's [AllowAnonymous] gate — instead of the
/// always-authenticated default fixture, which cannot see a regression there.
/// </summary>
public class TestAnonymousAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
