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

using System.Text;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Verification;

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
/// <para>
/// Under a warn-only <see cref="PluginTrustMode"/> a signature that would
/// fail is logged and the package is trusted instead, with the same message.
/// </para>
/// </summary>
public class SignatureVerificationStage(
    IPluginTrustedKeys trustedKeys,
    PluginTrustMode? mode = null
) : IPluginVerificationStage
{
    private readonly PluginTrustMode _mode = mode ?? PluginTrustMode.Enforcing;

    public SignatureVerificationStage()
        : this(PluginTrustedKeys.None) { }

    public string Name => "Signature";

    public bool Enforced => true;

    public (PluginStageOutcome Outcome, string? Message) Evaluate(PluginVerificationContext context)
    {
        (PluginStageOutcome outcome, string? message) = Check(context);

        if (outcome != PluginStageOutcome.Fail || _mode.Enforce)
            return (outcome, message);

        _mode.WouldRefuse(
            $"{context.Manifest.Name} {context.Manifest.Version}",
            "signature",
            message ?? string.Empty
        );

        return (PluginStageOutcome.Trust, message);
    }

    private (PluginStageOutcome Outcome, string? Message) Check(PluginVerificationContext context)
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

        if (context.IndexSignature is { } index)
            return CheckIndex(context, index);

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

    // The signed subject holds the sha256, so the package is tied to the
    // signature through that hash rather than through its own bytes.
    private (PluginStageOutcome Outcome, string? Message) CheckIndex(
        PluginVerificationContext context,
        PluginIndexSignature index
    )
    {
        if (MissingField(index) is { } missing)
            return (
                PluginStageOutcome.Fail,
                $"The repository index signs this version but leaves out {missing}, which is part of what was signed, so the signature cannot be checked. Refresh the repositories; if it stays this way the repository has to publish {missing}."
            );

        string? publicKey = trustedKeys.Find(index.KeyId!);

        if (publicKey is null)
            return (
                PluginStageOutcome.Fail,
                $"The signature names key {index.KeyId}, which this server does not trust. A key it has never seen is not a key it can vouch for."
            );

        if (context.PackagePath is null || !File.Exists(context.PackagePath))
            return (PluginStageOutcome.Fail, "There is no package to check the signature against.");

        byte[] subject = Encoding.UTF8.GetBytes(PluginSignatureSubject.Build(index));

        if (!PluginEd25519.Verify(subject, index.Signature, publicKey))
            return (
                PluginStageOutcome.Fail,
                "The signature in the repository index does not match what the index says about this version. Its id, version, checksum, publisher, tier or capabilities changed after it was signed, or it was not signed by the key it names. Refresh the repositories and install again."
            );

        string actual = PluginPackageChecksum.Of(context.PackagePath);

        return string.Equals(actual, index.Sha256!.Trim(), StringComparison.OrdinalIgnoreCase)
            ? (PluginStageOutcome.Pass, null)
            : (
                PluginStageOutcome.Fail,
                $"The downloaded package is not the one that was signed. It hashes to {actual} and the signed sha256 is {index.Sha256.Trim()}. Install it again; if it still differs, the repository is serving a different file than it signed."
            );
    }

    private static string? MissingField(PluginIndexSignature index)
    {
        if (string.IsNullOrWhiteSpace(index.KeyId))
            return "kid";
        if (string.IsNullOrWhiteSpace(index.PublisherId))
            return "publisherId";
        if (string.IsNullOrWhiteSpace(index.Tier))
            return "tier";
        if (string.IsNullOrWhiteSpace(index.Sha256))
            return "checksum";

        return null;
    }
}
