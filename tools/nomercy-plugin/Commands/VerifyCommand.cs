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

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Cli;

/// <summary>
/// The scan the marketplace runs, run locally first.
/// </summary>
public static class VerifyCommand
{
    /// <param name="path">A plugin folder, or the zip pack produced.</param>
    public static Task<ScanReport> RunAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new ScanReport { Refusals = ManifestScan.Run(path) });

        string unpacked = Path.Combine(
            Path.GetTempPath(),
            "nomercy-verify-" + Path.GetRandomFileName()
        );
        string name = Path.GetFileName(path);

        try
        {
            ZipFile.ExtractToDirectory(path, unpacked);

            // The shallowest manifest wins, as it does when the server installs.
            string folder = Directory
                .GetFiles(unpacked, "plugin.json", SearchOption.AllDirectories)
                .OrderBy(file => file.Count(character => character is '/' or '\\'))
                .Select(file => Path.GetDirectoryName(file)!)
                .FirstOrDefault(unpacked);

            // The temp folder is an implementation detail; the author knows the zip.
            IReadOnlyList<PluginRefusal> refusals = ManifestScan
                .Run(folder)
                .Select(refusal =>
                    refusal with
                    {
                        Plugin = refusal.Plugin == folder ? name : refusal.Plugin,
                    }
                )
                .ToList();

            return Task.FromResult(new ScanReport { Refusals = refusals });
        }
        catch (InvalidDataException)
        {
            return Task.FromResult(
                new ScanReport
                {
                    Refusals =
                    [
                        new PluginRefusal(
                            PluginRefusalCodes.ManifestInvalid,
                            name,
                            "The package is not a readable zip file.",
                            "A package that cannot be opened cannot be checked, and a server could not install it.",
                            "Build it again with nomercy-plugin pack.",
                            PluginRefusalSeverity.Blocked
                        ),
                    ],
                }
            );
        }
        finally
        {
            try
            {
                Directory.Delete(unpacked, recursive: true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Prints it. Human by default and JSON when a build asked for it, because
    /// a CI step that has to parse a paragraph is one that breaks on a reword.
    /// The JSON is the object the marketplace scanner reads.
    /// </summary>
    public static void Print(ScanReport report, bool asJson, TextWriter writer)
    {
        if (asJson)
        {
            JsonArray findings = [];
            foreach (PluginRefusal refusal in report.Refusals)
            {
                JsonNode finding = JsonSerializer.SerializeToNode(refusal)!;
                finding["marketplaceCode"] = MarketplaceCodeMap.For(refusal.Code);
                findings.Add(finding);
            }

            JsonObject root = new()
            {
                ["passed"] = report.Passed,
                ["findings"] = findings,
                ["capabilitiesUsed"] = new JsonArray(),
                ["dependencies"] = new JsonArray(),
            };

            writer.WriteLine(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
