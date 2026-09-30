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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Verification;

/// <summary>
/// Reads every DLL a plugin ships before any of it is mapped into the process:
/// the folder beside the entry assembly (and <c>runtimes/</c>, where native
/// code lives), or every entry of the archive when the install verifies the
/// zip before unpacking. An unreadable file is a refusal. A plugin author runs
/// this stage on their build output to see the same list the server does.
/// </summary>
public sealed class CodeScanVerificationStage : IPluginVerificationStage
{
    public string Name => "CodeScan";
    public bool Enforced => true;

    public (PluginStageOutcome Outcome, string? Message) Evaluate(PluginVerificationContext context)
    {
        bool insideArchive =
            context.PackagePath is { } package
            && context.AssemblyPath.Length > package.Length
            && context.AssemblyPath.StartsWith(package, StringComparison.OrdinalIgnoreCase);

        string? refusal = insideArchive
            ? RefuseArchive(context.PackagePath!, Path.GetFileName(context.AssemblyPath))
            : Refuse(context.AssemblyPath);

        return refusal is null
            ? (PluginStageOutcome.Pass, null)
            : (PluginStageOutcome.Fail, refusal);
    }

    /// <summary>
    /// The refusal for the folder around <paramref name="entryDllPath"/>, or
    /// null when every DLL in it is clean. The bare-assembly load calls this
    /// on the shadow copy, right before its load context is built.
    /// </summary>
    internal static string? Refuse(string entryDllPath)
    {
        string entry = Path.GetFullPath(entryDllPath);
        string folder = Path.GetDirectoryName(entry)!;
        if (!File.Exists(entry))
            return $"{PluginRefusalCode.CodeScan}: {Path.GetFileName(entry)}: unreadable: missing";

        string runtimes = Path.Combine(folder, "runtimes");
        IEnumerable<string> files = Directory.EnumerateFiles(folder, "*.dll");
        if (Directory.Exists(runtimes))
            files = files.Concat(
                Directory.EnumerateFiles(runtimes, "*.dll", SearchOption.AllDirectories)
            );

        List<string> refusals = [];
        foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            bool isEntry = string.Equals(
                Path.GetFullPath(file),
                entry,
                StringComparison.OrdinalIgnoreCase
            );
            Collect(refusals, Path.GetRelativePath(folder, file), PluginCodeScanner.Scan(file, isEntry));
        }

        return Format(refusals);
    }

    private static string? RefuseArchive(string zipPath, string entryName)
    {
        List<string> refusals = [];
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                using MemoryStream bytes = new();
                using (Stream open = entry.Open())
                    open.CopyTo(bytes);
                bytes.Position = 0;

                bool isEntry = string.Equals(entry.Name, entryName, StringComparison.OrdinalIgnoreCase);
                Collect(refusals, entry.FullName, PluginCodeScanner.Scan(bytes, isEntry));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            refusals.Add($"{Path.GetFileName(zipPath)}: unreadable: {ex.GetType().Name}");
        }

        return Format(refusals);
    }

    private static void Collect(List<string> refusals, string file, IReadOnlyList<string> findings)
    {
        foreach (string finding in findings)
            refusals.Add($"{file}: {finding}");
    }

    private static string? Format(List<string> refusals) =>
        refusals.Count == 0 ? null : $"{PluginRefusalCode.CodeScan}: {string.Join("; ", refusals)}";
}
