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

/// <summary>
/// Refuses a plugin folder whose files are not the ones the install recorded:
/// a changed, added or missing file, or a folder that was never installed
/// through the server and so has no record at all.
/// </summary>
public sealed class FileManifestVerificationStage : IPluginVerificationStage
{
    public string Name => "FileManifest";
    public bool Enforced => true;

    public (PluginStageOutcome Outcome, string? Message) Evaluate(PluginVerificationContext context)
    {
        // Install time: the files are still inside the archive the checksum
        // and signature stages cover, and the record is written after unpack.
        if (context.PackagePath is not null)
            return (PluginStageOutcome.Pass, null);

        string? refusal = Refuse(context.AssemblyPath);

        return refusal is null
            ? (PluginStageOutcome.Pass, null)
            : (PluginStageOutcome.Fail, refusal);
    }

    /// <summary>
    /// The refusal for the installed folder around <paramref name="entryDllPath"/>,
    /// or null when it holds exactly the recorded files. The bare-assembly load
    /// has no manifest and never meets the verifier, so it calls this directly.
    /// </summary>
    internal static string? Refuse(string entryDllPath)
    {
        IReadOnlyList<string> findings = PluginFileManifest.Check(
            Path.GetDirectoryName(Path.GetFullPath(entryDllPath))!
        );

        return findings.Count == 0
            ? null
            : $"{PluginRefusalCode.FilesChanged}: {string.Join("; ", findings)}";
    }
}
