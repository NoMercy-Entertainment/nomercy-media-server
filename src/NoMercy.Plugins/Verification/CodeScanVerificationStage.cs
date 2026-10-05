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
/// Reads every executable image a plugin ships before any of it is mapped into
/// the process: every file under the entry assembly's folder, at any depth
/// and under any name, or every entry of the archive when the install
/// verifies the zip before unpacking. An unreadable file is a refusal. A
/// plugin author runs this stage on their build output to see the same list
/// the server does.
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
    /// null when every image in it is clean. The bare-assembly load calls this
    /// on the shadow copy, right before its load context is built.
    /// </summary>
    internal static string? Refuse(string entryDllPath)
    {
        string entry = Path.GetFullPath(entryDllPath);
        string folder = Path.GetDirectoryName(entry)!;
        if (!File.Exists(entry))
            return $"{PluginRefusalCode.CodeScan}: {Path.GetFileName(entry)}: unreadable: missing";

        List<string> refusals = [];
        IEnumerable<string> files = Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            using FileStream stream = File.OpenRead(file);
            if (!IsImage(file, stream))
                continue;

            bool isEntry = string.Equals(
                Path.GetFullPath(file),
                entry,
                StringComparison.OrdinalIgnoreCase
            );
            Collect(
                refusals,
                Path.GetRelativePath(folder, file),
                PluginCodeScanner.Scan(stream, Path.GetFileName(file), isEntry)
            );
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
                if (entry.Name.Length == 0)
                    continue;

                using MemoryStream bytes = new();
                using (Stream open = entry.Open())
                    open.CopyTo(bytes);
                if (!IsImage(entry.Name, bytes))
                    continue;

                bool isEntry = string.Equals(
                    entry.Name,
                    entryName,
                    StringComparison.OrdinalIgnoreCase
                );
                Collect(
                    refusals,
                    entry.FullName,
                    PluginCodeScanner.Scan(bytes, entry.Name, isEntry)
                );
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            refusals.Add($"{Path.GetFileName(zipPath)}: unreadable: {ex.GetType().Name}");
        }

        return Format(refusals);
    }

    /// <summary>
    /// The load context serves any file in the folder, whatever it is called,
    /// so the name is a hint and the bytes decide: a <c>.dll</c> or
    /// <c>.exe</c>, or any file that starts with the PE signature <c>MZ</c>.
    /// Leaves the stream at its start.
    /// </summary>
    private static bool IsImage(string fileName, Stream stream)
    {
        Span<byte> head = stackalloc byte[2];
        stream.Position = 0;
        int read = stream.ReadAtLeast(head, 2, throwOnEndOfStream: false);
        stream.Position = 0;

        return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || (read == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z');
    }

    private static void Collect(List<string> refusals, string file, IReadOnlyList<string> findings)
    {
        foreach (string finding in findings)
            refusals.Add($"{file}: {finding}");
    }

    private static string? Format(List<string> refusals) =>
        refusals.Count == 0 ? null : $"{PluginRefusalCode.CodeScan}: {string.Join("; ", refusals)}";
}
