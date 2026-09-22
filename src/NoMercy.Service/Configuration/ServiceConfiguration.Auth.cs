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

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NoMercy.Authorization;
using NoMercy.Networking.Http;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.PluginSdk.Hooks;
using NoMercy.Service.Authorization;
using NoMercy.Setup.Auth;
using Serilog.Events;

namespace NoMercy.Service.Configuration;

public static partial class ServiceConfiguration
{
    private static void ConfigureAuth(IServiceCollection services)
    {
        // Configure Authorization.
        //
        // The "api" policy is ALSO the default policy: a bare [Authorize] must mean
        // "a token for a user provisioned on THIS server", not merely "any valid
        // token". Keycloak issues one shared audience ("nomercy-server") across all
        // installs, so without the local-user check below a token minted for any
        // NoMercy account is accepted by every server (cross-tenant). No scheme is
        // pinned so the ambient authenticated principal is used (JWT in production,
        // the test scheme under WebApplicationFactory). Scope is checked split-aware
        // via ApiScopePolicy because the real token carries one space-delimited claim.
        AuthorizationPolicy apiPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(context => ApiScopePolicy.HasRequiredScope(context.User))
            .RequireAssertion(context =>
            {
                string? sub = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(sub, out Guid userId))
                    return false;

                return UserCache.Current.Users.Any(user => user.Id == userId);
            })
            .Build();

        services.AddAuthorizationBuilder().SetDefaultPolicy(apiPolicy).AddPolicy("api", apiPolicy);

        // Permission policies backed by IMediaAuthorizationPolicy so endpoints can
        // declare [Authorize(Policy = ...)] instead of imperative permission checks.
        services
            .AddAuthorizationBuilder()
            .AddPolicy(
                "Owner",
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new OwnerRequirement());
                }
            )
            .AddPolicy(
                "Moderator",
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new ModeratorRequirement());
                }
            )
            .AddPolicy(
                "MediaAccess",
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new MediaAccessRequirement());
                }
            )
            .AddPolicy(
                "OpticalAccess",
                policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new OpticalAccessRequirement());
                }
            );

        services.AddScoped<IAuthorizationHandler, MediaAuthorizationHandler>();
        services.AddSingleton<
            IAuthorizationMiddlewareResultHandler,
            ProblemDetailsAuthorizationResultHandler
        >();

        // Eagerly load cached signing key so it's available before auth init completes
        OfflineJwksCache.LoadCachedPublicKey();

        // Configure Authentication
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = ExternalServicesConfig.Current.AuthBaseUrl;
                options.RequireHttpsMetadata =
                    ExternalServicesConfig.Current.AuthBaseUrl.StartsWith(
                        "https://",
                        StringComparison.OrdinalIgnoreCase
                    );
                options.Audience = ExternalServicesConfig.Current.TokenClientId;

                // Enable offline token validation via cached signing keys
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                // Keycloak's real `iss` claim (and its .well-known discovery document)
                // never carries a trailing slash, but AuthBaseUrl is configured WITH one
                // (it's concatenated straight into URLs like "{AuthBaseUrl}protocol/
                // openid-connect/token" elsewhere) — an exact-match ValidIssuer against
                // the slash-terminated form rejected every real token with "issuer is
                // invalid", failing auth for every client hub (device/video/ripper/
                // dashboard) network-wide. Accept both forms rather than picking one.
                string authBaseUrl = ExternalServicesConfig.Current.AuthBaseUrl;
                options.TokenValidationParameters.ValidIssuers =
                [
                    authBaseUrl,
                    authBaseUrl.TrimEnd('/'),
                ];

                // Explicitly enforce audience validation. options.Audience already sets
                // ValidAudience; this line makes the intent unambiguous and guards against
                // future refactors that might inadvertently remove the Audience assignment.
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.IssuerSigningKeyResolver = (
                    token,
                    securityToken,
                    kid,
                    parameters
                ) =>
                {
                    // Use the OIDC-discovered keys first (fresh from Keycloak).
                    // Add the cached key as a fallback for offline/key-rotation scenarios.
                    List<SecurityKey> keys = parameters.IssuerSigningKeys?.ToList() ?? [];
                    RsaSecurityKey? cachedKey = OfflineJwksCache.CachedSigningKey;
                    if (cachedKey is not null && keys.All(k => k != cachedKey))
                        keys.Add(cachedKey);

                    return keys;
                };

                options.Events = new()
                {
                    OnMessageReceived = context =>
                    {
                        StringValues accessToken = context.Request.Query["access_token"];
                        string[] result = accessToken.ToString().Split('&');

                        string? token =
                            result.Length > 0 && !string.IsNullOrEmpty(result[0])
                                ? result[0]
                                : null;

                        if (token is not null)
                        {
                            while (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                                token = token["Bearer ".Length..];

                            context.Token = token;
                        }
                        else
                        {
                            // If not in query, check header for double Bearer
                            string? authHeader =
                                context.Request.Headers.Authorization.FirstOrDefault();
                            if (
                                authHeader is not null
                                && authHeader.StartsWith(
                                    "Bearer Bearer ",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                string tokenFromHeader = authHeader;
                                while (
                                    tokenFromHeader.StartsWith(
                                        "Bearer ",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                    tokenFromHeader = tokenFromHeader["Bearer ".Length..];

                                context.Token = tokenFromHeader;
                            }
                        }

                        return Task.CompletedTask;
                    },
                    // OnTokenValidated fires on EVERY authenticated request (every API call,
                    // every SignalR frame), not on actual login. Logging "auth.login" here
                    // produced one row per request and flooded the activity log with
                    // duplicates and FK-failing Ulid.Empty deviceIds. Real login events live
                    // at Keycloak; the server has no notion of "login" via JWT validation.
                    // If we ever want session timeline data, log "session_start" from
                    // ConnectionHub.OnConnectedAsync (one row per actual hub connection).
                    //
                    // Runs ONLY after Keycloak's own signing-key/issuer/audience validation
                    // (above) has already succeeded. It may only ADD claims to the already-
                    // authenticated identity — it must never call context.Fail(...), never
                    // replace context.Principal, and never short-circuit. A plugin cannot
                    // grant access; it can only enrich a principal Keycloak already accepted.
                    // Keep this cheap: it is a hot path, not a login event.
                    OnTokenValidated = async context =>
                    {
                        if (context.Principal?.Identity is not ClaimsIdentity identity)
                            return;

                        IPluginClaimsAugmentor augmentor =
                            context.HttpContext.RequestServices.GetRequiredService<IPluginClaimsAugmentor>();

                        string token =
                            (context.SecurityToken as JsonWebToken)?.EncodedToken ?? string.Empty;
                        IReadOnlyList<Claim> extraClaims =
                            await augmentor.CollectAdditionalClaimsAsync(
                                token,
                                context.HttpContext.RequestAborted
                            );

                        foreach (Claim claim in extraClaims)
                            if (!identity.HasClaim(claim.Type, claim.Value))
                                identity.AddClaim(claim);
                    },
                    OnAuthenticationFailed = context =>
                    {
                        HttpRequest req = context.Request;

                        // Extract client identity from query string (sent by all hub connections)
                        string clientName =
                            req.Query["client_name"].FirstOrDefault()
                            ?? req.Query["custom_name"].FirstOrDefault()
                            ?? "unknown-client";
                        string clientType = req.Query["client_type"].FirstOrDefault() ?? "unknown";
                        string clientDevice = req.Query["client_device"].FirstOrDefault() ?? "";
                        string clientOs = req.Query["client_os"].FirstOrDefault() ?? "";
                        string remoteIp = context.HttpContext.ClientIp()?.ToString() ?? "unknown";

                        // Build a human-readable client description
                        string client = !string.IsNullOrEmpty(clientDevice)
                            ? $"\"{clientName}\" ({clientDevice}, {clientOs})"
                            : $"\"{clientName}\" ({clientType})";

                        // Extract the hub/endpoint name from the path
                        string endpoint =
                            req.Path.Value?.Split('/')
                                .FirstOrDefault(s =>
                                    s.EndsWith("Hub", StringComparison.OrdinalIgnoreCase)
                                    || s == "negotiate"
                                    || s.Length > 0
                                )
                            ?? req.Path.Value
                            ?? "unknown";
                        // Get just the hub name from the path e.g. /videoHub/negotiate → videoHub
                        string[] segments = (req.Path.Value ?? "").Trim('/').Split('/');
                        string hub = segments.Length > 0 ? segments[0] : "unknown";

                        // Try to read token claims for expiry diagnostics
                        string tokenAge = "";
                        try
                        {
                            string? raw = req.Query["access_token"].FirstOrDefault()?.Split('&')[0];
                            if (string.IsNullOrEmpty(raw))
                            {
                                // Check Authorization header
                                string? authHeader = req.Headers.Authorization.FirstOrDefault();
                                if (
                                    authHeader?.StartsWith(
                                        "Bearer ",
                                        StringComparison.OrdinalIgnoreCase
                                    ) == true
                                )
                                    raw = authHeader["Bearer ".Length..];
                            }

                            if (!string.IsNullOrEmpty(raw))
                            {
                                if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                                    raw = raw["Bearer ".Length..];

                                JwtSecurityTokenHandler handler = new();
                                JwtSecurityToken jwt = handler.ReadJwtToken(raw);
                                TimeSpan expired = DateTime.UtcNow - jwt.ValidTo;
                                tokenAge =
                                    expired.TotalHours >= 1
                                        ? $" (token expired {expired.TotalHours:F0}h ago)"
                                        : $" (token expired {expired.TotalMinutes:F0}m ago)";
                            }
                        }
                        catch
                        { /* token unreadable — skip */
                        }

                        // Human-readable failure reason
                        string reason = context.Exception switch
                        {
                            SecurityTokenExpiredException => $"Expired token{tokenAge}",
                            SecurityTokenInvalidSignatureException => "Invalid token signature",
                            SecurityTokenInvalidAudienceException => "Token audience mismatch",
                            SecurityTokenInvalidIssuerException => "Token issuer mismatch",
                            _ =>
                                $"{context.Exception.GetType().Name}: {context.Exception.Message}{(context.Exception.InnerException != null ? $" (Inner: {context.Exception.InnerException.Message})" : "")}",
                        };

                        Logger.Auth(
                            $"{reason} — {client} → {hub} from {remoteIp}",
                            LogEventLevel.Warning
                        );

                        // Activity log write removed — OnAuthenticationFailed fires for every
                        // expired-token request (effectively continuously for any idle client),
                        // not just real login failures. Real failures live at Keycloak. Diagnostic
                        // log above is enough for server-side visibility.
                        return Task.CompletedTask;
                    },
                };
            });
    }
}
