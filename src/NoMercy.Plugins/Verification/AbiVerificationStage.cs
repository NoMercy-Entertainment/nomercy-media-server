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

public class AbiVerificationStage : IPluginVerificationStage
{
    public string Name => "ABI";
    public bool Enforced => true;

    public (PluginStageOutcome Outcome, string? Message) Evaluate(PluginVerificationContext context)
    {
        if (PluginAbi.IsCompatible(context.Manifest.TargetAbi))
            return (PluginStageOutcome.Pass, null);

        return (
            PluginStageOutcome.Fail,
            $"ABI '{context.Manifest.TargetAbi}' is incompatible with server ABI {PluginAbi.Current}."
        );
    }
}
