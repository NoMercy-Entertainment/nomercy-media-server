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

using NoMercy.PluginSdk.Sideload;

namespace NoMercy.PluginSdk.Verification;

/// <summary>
/// One SHA-256 per file of an installed plugin, written beside it when the
/// verified archive is unpacked and compared at every load.
/// <para>
/// The checksum and the signature prove the zip, and stop there. The files
/// that load are the ones on disk, so the install writes down what it
/// unpacked and a load that finds anything else refuses. Plain
/// <c>sha256sum -c</c> lines rather than JSON: an owner can check the folder
/// by hand, and the parser is four lines.
/// </para>
/// </summary>
internal static class PluginFileManifest
{
    public const string FileName = ".files.sha256";

    private static readonly string[] Unhashed = [FileName, PluginSideloadMarker.FileName];

    public static async Task WriteAsync(string pluginFolder, CancellationToken ct = default)
    {
        List<string> lines = [];
        foreach ((string path, string file) in Files(pluginFolder))
            lines.Add($"{await PluginPackageChecksum.OfAsync(file, ct)}  {path}");

        await File.WriteAllLinesAsync(Path.Combine(pluginFolder, FileName), lines, ct);
    }

    /// <summary>
    /// Every way the folder differs from its record, or one line saying there
    /// is no record. Empty when the folder is exactly what was installed.
    /// </summary>
    public static IReadOnlyList<string> Check(string pluginFolder)
    {
        string manifestPath = Path.Combine(pluginFolder, FileName);
        if (!File.Exists(manifestPath))
            return ["no file manifest"];

        Dictionary<string, string> recorded = File.ReadAllLines(manifestPath)
            .Where(line => line.Length > 66)
            .ToDictionary(line => line[66..], line => line[..64], StringComparer.Ordinal);

        List<string> findings = [];
        foreach ((string path, string file) in Files(pluginFolder))
        {
            if (!recorded.Remove(path, out string? hash))
                findings.Add($"added: {path}");
            else if (!string.Equals(hash, PluginPackageChecksum.Of(file), StringComparison.Ordinal))
                findings.Add($"changed: {path}");
        }

        findings.AddRange(
            recorded.Keys.Order(StringComparer.Ordinal).Select(path => $"missing: {path}")
        );

        return findings;
    }

    private static IEnumerable<(string Path, string File)> Files(string pluginFolder) =>
        Directory
            .EnumerateFiles(pluginFolder, "*", SearchOption.AllDirectories)
            .Select(file =>
                (Path: Path.GetRelativePath(pluginFolder, file).Replace('\\', '/'), File: file)
            )
            // A single-dll swap leaves its backup beside the plugin until the
            // next start clears it; a record that listed it would then say
            // "missing" for a file the server itself removed.
            .Where(entry =>
                !Unhashed.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)
                && !entry.Path.EndsWith(PluginManager.RollbackSuffix, StringComparison.Ordinal)
            )
            .OrderBy(entry => entry.Path, StringComparer.Ordinal);
}
