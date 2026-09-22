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

using System.Text.Json;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Cli;

/// <summary>
/// The scan the marketplace runs, run locally first.
/// </summary>
public static class VerifyCommand
{
    public static Task<ScanReport> RunAsync(string folder, CancellationToken ct = default)
    {
        return Task.FromResult(new ScanReport { Refusals = ManifestScan.Run(folder) });
    }

    /// <summary>
    /// Prints it. Human by default and JSON when a build asked for it, because
    /// a CI step that has to parse a paragraph is one that breaks on a reword.
    /// </summary>
    public static void Print(ScanReport report, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            writer.WriteLine(
                JsonSerializer.Serialize(
                    report.Refusals,
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
            return;
        }

        foreach (PluginRefusal refusal in report.Refusals)
        {
            writer.WriteLine($"{refusal.Severity.ToString().ToUpperInvariant()} {refusal.Code}");
            writer.WriteLine($"  {refusal.What}");
            writer.WriteLine($"  {refusal.Why}");
            writer.WriteLine($"  {refusal.Fix}");
            writer.WriteLine();
        }

        if (report.Refusals.Count == 0)
            writer.WriteLine("Nothing to report.");
    }
}
