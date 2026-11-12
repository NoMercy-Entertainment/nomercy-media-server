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

using NoMercy.Storage;

namespace NoMercy.Encoder.Orchestration;

/// <summary>
/// The one formula that maps an encode's storage-relative output directory to
/// its working folder under <see cref="StoragePaths.TranscodeRoot"/>. The
/// orchestrator, the coordinator's finalize pass and the orphan sweeper must
/// agree on it, or the sweeper deletes a folder a job still owns.
/// </summary>
public static class EncoderTempDirectory
{
    /// <summary>
    /// The mirrored working folder for <paramref name="outputDirectory"/>, or
    /// null when it is empty (an empty directory has no stable folder).
    /// </summary>
    public static string? MirroredPath(string? outputDirectory)
    {
        string relativeOutputPath = (outputDirectory ?? string.Empty).Replace('\\', '/').Trim('/');

        return string.IsNullOrEmpty(relativeOutputPath)
            ? null
            : Path.Combine(
                StoragePaths.TranscodeRoot,
                relativeOutputPath.Replace('/', Path.DirectorySeparatorChar)
            );
    }

    /// <summary>
    /// <see cref="MirroredPath"/>, or a fresh opaque <c>nomercy-enc-&lt;ulid&gt;</c>
    /// folder when <paramref name="outputDirectory"/> is empty.
    /// </summary>
    public static string Resolve(string? outputDirectory) =>
        MirroredPath(outputDirectory)
        ?? Path.Combine(
            StoragePaths.TranscodeRoot,
            $"{TranscodeRootOrphanSweeper.EncWorkingDirPrefix}{Ulid.NewUlid()}"
        );
}
