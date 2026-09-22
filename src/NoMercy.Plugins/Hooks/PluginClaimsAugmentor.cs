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
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk.Hooks;

/// <summary>
/// Collects ONLY additional claims from every active <see cref="IAuthPlugin"/> that
/// declares the <c>auth</c> capability. Never denies and never replaces identity —
/// Keycloak's own signature/issuer/audience validation is the sole authority on
/// whether a token is authenticated; a plugin only gets to enrich an already-valid
/// principal. A plugin that throws, hangs past <see cref="PerPluginTimeout"/>, omits
/// the capability, or tries to hand out a reserved (identity/role/scope/standard-JWT)
/// claim type is ignored/stripped so a broken or malicious plugin can never weaken
/// auth. This runs on every authenticated request/SignalR frame, so a hang must never
/// propagate past the per-plugin timeout.
/// </summary>
public class PluginClaimsAugmentor(
    IPluginManager pluginManager,
    ILogger<PluginClaimsAugmentor> logger
) : IPluginClaimsAugmentor
{
    // Claim types a plugin can never forge, regardless of what it declares — identity,
    // role/scope, and the standard registered JWT claims. A plugin may only contribute
    // its OWN custom claim types (e.g. "plan"); enforced here so it holds even if a
    // future caller other than OnTokenValidated ever consumes this augmentor's output.
    private static readonly HashSet<string> ReservedClaimTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ClaimTypes.NameIdentifier,
        ClaimTypes.Role,
        "role",
        "scope",
        "sub",
        "aud",
        "iss",
        "exp",
        "iat",
        "nbf",
    };

    public TimeSpan PerPluginTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<Claim>> CollectAdditionalClaimsAsync(
        string token,
        CancellationToken ct
    )
    {
        List<Claim> claims = [];

        foreach (IAuthPlugin plugin in pluginManager.GetPluginsOfType<IAuthPlugin>())
        {
            PluginCapabilities? capabilities = pluginManager.GetPluginInfo(plugin.Id)?.Capabilities;

            if (!PluginCapabilityGuard.DeclaresHook(capabilities, PluginHookCapability.Auth))
                continue;

            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerPluginTimeout);

            try
            {
                AuthResult result = await plugin.AuthenticateAsync(token, timeoutCts.Token);
                if (!result.IsAuthenticated)
                    continue;

                foreach (KeyValuePair<string, string> claim in result.Claims)
                {
                    if (ReservedClaimTypes.Contains(claim.Key))
                        continue;

                    claims.Add(new Claim(claim.Key, claim.Value));
                }
            }
            catch (Exception ex)
            {
                if (PluginStaleMemberLog.Explain(logger, plugin.Id, ex, "adding claims"))
                    continue;

                logger.LogWarning(
                    ex,
                    "Auth plugin {Plugin} failed or timed out; ignoring its claims (auth is never weakened).",
                    plugin.Id
                );
            }
        }

        return claims;
    }
}
