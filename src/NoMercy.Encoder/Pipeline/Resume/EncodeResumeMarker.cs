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

                FileInfo file = new(command.Arguments[i + 1]);
                if (file.Exists)
                    yield return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            }
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
