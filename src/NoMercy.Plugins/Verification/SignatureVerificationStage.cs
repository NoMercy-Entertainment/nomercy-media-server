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
/// Whether the publisher actually signed what arrived.
/// <para>
/// A checksum says the bytes did not change on the way. It says nothing about
/// who produced them: anyone who can serve the file can serve a checksum for
/// it too. A signature is the only part of this that answers "and who made
/// this", which is the question that matters when the file is about to run
/// inside the server.
/// </para>
/// <para>
/// Enforced for marketplace installs only. A file the owner dropped into the
/// plugins folder themselves is their own decision, and refusing it would take
/// away the one path that works while the marketplace does not exist yet.
/// </para>
/// </summary>
public class SignatureVerificationStage(IPluginTrustedKeys trustedKeys) : IPluginVerificationStage
{
    public SignatureVerificationStage()
        : this(PluginTrustedKeys.None) { }

    public string Name => "Signature";

    public bool Enforced => true;

    public (PluginStageOutcome Outcome, string? Message) Evaluate(PluginVerificationContext context)
    {
        if (!context.FromMarketplace)
            return (PluginStageOutcome.Pass, null);

        // No keys shipped yet. Failing every marketplace install here would
        // take the marketplace offline the day it opened; passing silently
        // would mean the stage never did anything. Trust records that the
        // question was asked and not answered.
        if (!trustedKeys.Any)
            return (
                PluginStageOutcome.Trust,
                "This server holds no publisher keys yet, so the signature was not checked."
            );

        if (context.Signature is null)
            return (
                PluginStageOutcome.Fail,
                "The repository offered this package without a signature. A marketplace package is signed by its publisher; an unsigned one has nothing to say who built it."
            );

        if (
            !string.Equals(
                context.Signature.Algorithm,
                PluginEd25519.Algorithm,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return (
                PluginStageOutcome.Fail,
                $"The signature uses {context.Signature.Algorithm}, which this server does not verify. It reads ed25519."
            );

        string? publicKey = trustedKeys.Find(context.Signature.KeyId);

        if (publicKey is null)
            return (
                PluginStageOutcome.Fail,
                $"The signature names key {context.Signature.KeyId}, which this server does not trust. A key it has never seen is not a key it can vouch for."
            );

        if (context.PackagePath is null || !File.Exists(context.PackagePath))
            return (PluginStageOutcome.Fail, "There is no package to check the signature against.");

        byte[] content = File.ReadAllBytes(context.PackagePath);

        return PluginEd25519.Verify(content, context.Signature.Value, publicKey)
            ? (PluginStageOutcome.Pass, null)
            : (
                PluginStageOutcome.Fail,
                "The signature does not match the package. Either the file changed after it was signed, or it was not signed by the key it names."
            );
    }
}
