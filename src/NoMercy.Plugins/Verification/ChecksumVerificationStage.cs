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

namespace NoMercy.Plugins.Verification;

public class ChecksumVerificationStage : IPluginVerificationStage
{
    public string Name => "Checksum";
    public bool Enforced => true;

    public (PluginStageOutcome Outcome, string? Message) Evaluate(PluginVerificationContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ExpectedChecksum))
            return (PluginStageOutcome.Pass, null);

        string? refusal = PluginPackageChecksum.Refuse(
            context.PackagePath,
            context.ExpectedChecksum
        );

        return refusal is null
            ? (PluginStageOutcome.Trust, null)
            : (PluginStageOutcome.Fail, refusal);
    }
}
