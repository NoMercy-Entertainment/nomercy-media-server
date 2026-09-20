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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Verification;

/// <summary>
/// Records that a plugin came from a repository the owner trusts.
/// <para>
/// Provenance, and only that. It used to enable such a plugin on install, which
/// let a repository flag answer the consent question on the owner's behalf for
/// every plugin that index ever lists. Consent exists so nothing starts
/// reaching the network on first install without the owner saying so, and where
/// a plugin came from is not the owner saying so. From Phase 3 trust skips the
/// marketplace review hold — a delay before a release is published — and never
/// a decision about the owner's own machine.
/// </para>
/// <para>
/// Provenance, never self-description: the manifest's author line is free text
/// any file can copy, so it decides nothing here. Where the plugin came from is
/// the owner's own configuration and it is the only input this reads.
/// </para>
/// </summary>
/// <remarks>
/// Takes a resolver rather than the repository itself, and asks for it only when
/// a plugin is being verified. The catalogue needs an HTTP stack; the verifier
/// runs in hosts that have none — a test, an embedded use — and building it
/// eagerly turned "this host cannot fetch an index" into "this host cannot load
/// plugins at all".
/// </remarks>
public class TrustedRepositoryVerificationStage(Func<IPluginRepository?> repository)
    : IPluginVerificationStage
{
    public string Name => "trusted-repository";

    /// <summary>
    /// Never enforced. This stage grants trust and never withholds it, so an
    /// index nobody could read costs a plugin nothing.
    /// </summary>
    public bool Enforced => false;

    public (PluginStageOutcome Outcome, string? Message) Evaluate(PluginVerificationContext context)
    {
        // No catalogue is not a reason to trust. It is a reason to ask.
        if (repository() is not { } catalogue)
            return (PluginStageOutcome.Pass, null);

        if (!catalogue.IsFromTrustedRepository(context.Manifest.Id.Value))
            return (PluginStageOutcome.Pass, null);

        return (
            PluginStageOutcome.Trust,
            $"{context.Manifest.Name} is listed by a repository this server trusts."
        );
    }
}
