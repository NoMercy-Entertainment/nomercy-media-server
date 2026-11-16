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
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Jobs;
using NoMercy.Encoder.Orchestration;
using NoMercy.Queue.MediaServer;

namespace NoMercy.MediaProcessing.Jobs;

/// <summary>
/// Looks up crash checkpoints for encoder queue jobs during orphan recovery.
/// Extracts the output directory from the serialized job payload, maps it to
/// the encode's working folder with <see cref="EncoderTempDirectory"/>, then
/// asks <see cref="ICheckpointStore"/> for a crash checkpoint there.
/// </summary>
public class EncoderOrphanCheckpointLookup(
    ICheckpointStore checkpointStore,
    ILogger<EncoderOrphanCheckpointLookup> logger
) : IOrphanCheckpointLookup
{
    public async Task<bool> HasCheckpointAsync(string jobPayload, CancellationToken ct = default)
    {
        // The execute stage saves the checkpoint in the encode's working folder,
        // not in the storage-relative output directory the payload carries.
        string? workingDirectory = EncoderTempDirectory.MirroredPath(
            ExtractOutputDirectory(jobPayload)
        );
        if (workingDirectory is null)
            return false;

        try
        {
            JobCheckpoint? checkpoint = await checkpointStore.LoadAsync(workingDirectory, ct);
            return checkpoint?.FailedAt is not null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to load checkpoint for WorkingDirectory={WorkingDirectory}",
                workingDirectory
            );
            return false;
        }
    }

    private static string? ExtractOutputDirectory(string payload)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            // The queue serializes payloads in camelCase, and the directory
            // lives on the coordinator state of a VideoEncodeJob.
            if (
                root.TryGetProperty("coordinator", out JsonElement coordinator)
                && coordinator.ValueKind == JsonValueKind.Object
                && coordinator.TryGetProperty("outputDirectory", out JsonElement dirElement)
                && dirElement.ValueKind == JsonValueKind.String
            )
            {
                return dirElement.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
