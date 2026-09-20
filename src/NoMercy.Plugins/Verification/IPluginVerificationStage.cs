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

public class PluginVerificationContext
{
    public required PluginManifest Manifest { get; init; }
    public required string AssemblyPath { get; init; }
    public string? ExpectedChecksum { get; init; }

    /// <summary>
    /// The artifact the server received, which is what
    /// <see cref="ExpectedChecksum"/> describes. Null when nothing was
    /// downloaded — a manual drop into the plugins folder, or a boot-time scan.
    /// </summary>
    public string? PackagePath { get; init; }
}

public enum PluginStageOutcome
{
    Pass,
    Fail,
    Trust,
}

public interface IPluginVerificationStage
{
    string Name { get; }
    bool Enforced { get; }
    (PluginStageOutcome Outcome, string? Message) Evaluate(PluginVerificationContext context);
}
