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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Verification;

public class PluginVerifier : IPluginVerifier
{
    private readonly List<IPluginVerificationStage> _stages;

    public PluginVerifier()
        : this([
            new AbiVerificationStage(),
            new ChecksumVerificationStage(),
            new SignatureVerificationStage(),
        ]) { }

    public PluginVerifier(List<IPluginVerificationStage> stages)
    {
        _stages = stages;
    }

    public PluginVerificationResult Verify(
        PluginManifest manifest,
        string assemblyPath,
        string? expectedChecksum,
        string? packagePath = null,
        bool fromMarketplace = false
    )
    {
        PluginVerificationContext context = new()
        {
            Manifest = manifest,
            AssemblyPath = assemblyPath,
            ExpectedChecksum = expectedChecksum,
            PackagePath = packagePath,
            Signature = manifest.Signature,
            FromMarketplace = fromMarketplace,
        };

        List<string> failures = [];
        bool trusted = false;

        foreach (IPluginVerificationStage stage in _stages)
        {
            (PluginStageOutcome outcome, string? message) = stage.Evaluate(context);

            if (outcome == PluginStageOutcome.Trust)
                trusted = true;
            else if (outcome == PluginStageOutcome.Fail && stage.Enforced)
                failures.Add(message ?? $"{stage.Name} stage failed.");
        }

        return new()
        {
            Verified = failures.Count == 0,
            Trusted = trusted,
            Failures = failures,
        };
    }
}
