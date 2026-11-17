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

using System.Security.Cryptography;
using System.Text;
using NoMercy.Encoder.Commands;
using NoMercy.Storage;

namespace NoMercy.Encoder.Pipeline.Resume;

/// <summary>
/// The <c>.resume.&lt;hash&gt;</c> file names the arguments and source that wrote the segments next to it.
/// </summary>
public class EncodeResumeMarker(IStorage storage)
{
    private const string Prefix = ".resume.";
    private const string ScopeTag = "scope:";

    public static string HashArguments(IEnumerable<FfmpegCommand> commands)
    {
        FfmpegCommand[] all = [.. commands];
        IEnumerable<string> input = all.SelectMany(c => c.Arguments).Concat(SourceStamps(all));
        string joined = string.Join('\n', input);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    // A source replaced under the same path has another length or write time.
    private static IEnumerable<string> SourceStamps(FfmpegCommand[] commands)
    {
        foreach (FfmpegCommand command in commands)
        {
            for (int i = 0; i < command.Arguments.Length - 1; i++)
            {
                if (command.Arguments[i] != "-i")
                    continue;

                if (StampOf(command.Arguments[i + 1]) is { } stamp)
                    yield return stamp;
            }
        }
    }

    // A URL, a pipe or a file that vanishes mid-read has no stamp; it must not fail the hash.
    private static string? StampOf(string argument)
    {
        try
        {
            FileInfo file = new(argument);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or ArgumentException
                        or NotSupportedException
                        or UnauthorizedAccessException
            )
        {
            return null;
        }
    }

    public string PathFor(string outputDirectory, string hash) =>
        storage.CombinePath(outputDirectory, Prefix + hash);

    public Task<bool> ExistsAsync(string markerPath, CancellationToken ct) =>
        storage.ExistsAsync(markerPath, ct);

    public Task WriteAsync(string markerPath, string startedBy, CancellationToken ct) =>
        storage.WriteAllTextAsync(markerPath, startedBy, ct);

    public async Task ClearAsync(string markerPath, CancellationToken ct)
    {
        if (await storage.ExistsAsync(markerPath, ct))
            await storage.DeleteAsync(markerPath, ct);
    }

    public static string ScopedContent(IEnumerable<string> segmentPrefixes, string startedBy) =>
        startedBy + "\n" + ScopeTag + string.Join('|', segmentPrefixes);

    /// <summary>Removes the markers that name one of these segment prefixes (or none), and the given stitch files.</summary>
    public async Task ClearOwnAsync(
        string outputDirectory,
        IReadOnlyCollection<string> segmentPrefixes,
        IEnumerable<string> stitchPaths,
        CancellationToken ct
    )
    {
        List<string> doomed = [];
        await foreach (StorageEntry entry in storage.ListAsync(outputDirectory, "*", true, ct))
        {
            if (
                entry.IsDirectory
                || !storage.GetName(entry.Path).StartsWith(Prefix, StringComparison.Ordinal)
            )
                continue;

            if (Owns(await storage.ReadAllTextAsync(entry.Path, ct), segmentPrefixes))
                doomed.Add(entry.Path);
        }

        foreach (string stitch in stitchPaths)
        {
            if (await storage.ExistsAsync(stitch, ct))
                doomed.Add(stitch);
        }

        foreach (string path in doomed)
            await storage.DeleteAsync(path, ct);
    }

    // A marker without a scope line predates it and is cleared like one of ours.
    private static bool Owns(string content, IReadOnlyCollection<string> segmentPrefixes)
    {
        string? scope = content
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith(ScopeTag, StringComparison.Ordinal));
        return scope is null || scope[ScopeTag.Length..].Split('|').Any(segmentPrefixes.Contains);
    }

    /// <summary>Removes every marker and stitch temp file; a finished bundle has none to resume.</summary>
    public async Task ClearAllAsync(string outputDirectory, CancellationToken ct)
    {
        List<string> markers = [];
        await foreach (StorageEntry entry in storage.ListAsync(outputDirectory, "*", true, ct))
        {
            string name = storage.GetName(entry.Path);
            if (
                !entry.IsDirectory
                && (
                    name.StartsWith(Prefix, StringComparison.Ordinal)
                    || name.EndsWith(EncodeResumeStitcher.StitchSuffix, StringComparison.Ordinal)
                )
            )
                markers.Add(entry.Path);
        }

        foreach (string marker in markers)
            await storage.DeleteAsync(marker, ct);
    }
}
