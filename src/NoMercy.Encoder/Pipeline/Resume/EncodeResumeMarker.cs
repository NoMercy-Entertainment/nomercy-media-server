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
/// The <c>.resume.&lt;hash&gt;</c> file in an encode's working folder. It names
/// the argument list that wrote the segments next to it, so a changed list
/// (a new profile, another source) never resumes onto segments it did not write.
/// </summary>
public class EncodeResumeMarker(IStorage storage)
{
    private const string Prefix = ".resume.";

    public static string HashArguments(IEnumerable<FfmpegCommand> commands)
    {
        string joined = string.Join('\n', commands.SelectMany(c => c.Arguments));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
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
}
