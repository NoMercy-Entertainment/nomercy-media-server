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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Encoder.Orchestration;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue;
using NoMercyQueue.Core;

namespace NoMercy.MediaProcessing.Jobs;

/// <summary>
/// Maps every waiting or running <see cref="VideoEncodeJob"/> in the queue to
/// the working folder it writes under the transcode root, so the boot sweep
/// keeps it. A coordinator that already recorded its output directory answers
/// from the payload; a job that has not started yet is resolved the way its
/// own Handle does, through the movie or episode folder name.
/// </summary>
public class QueuedEncoderTempDirectoryOwners(
    IDbContextFactory<QueueContext> queueContextFactory,
    IDbContextFactory<MediaContext> mediaContextFactory
) : IEncoderTempDirectoryOwners
{
    private static readonly string[] EncoderQueues =
    [
        QueueNames.Encoder,
        QueueNames.EncoderTask,
        QueueNames.EncoderGpu,
        QueueNames.EncoderCpu,
    ];

    public async Task<IReadOnlyCollection<string>> GetOwnedTempDirectoriesAsync(
        CancellationToken ct
    )
    {
        List<string> payloads;
        await using (QueueContext queueContext = await queueContextFactory.CreateDbContextAsync(ct))
        {
            payloads = await queueContext
                .QueueJobs.AsNoTracking()
                .Where(job => EncoderQueues.Contains(job.Queue))
                .Select(job => job.Payload)
                .ToListAsync(ct);
        }

        List<string> owned = [];
        MediaContext? mediaContext = null;

        try
        {
            foreach (string payload in payloads)
            {
                // A payload that cannot be read throws: the caller keeps every dir
                // when it cannot tell whose they are.
                if (SerializationHelper.Deserialize<object>(payload) is not VideoEncodeJob job)
                    continue;

                string? outputDirectory = job.Coordinator?.OutputDirectory;
                if (string.IsNullOrEmpty(outputDirectory))
                {
                    mediaContext ??= await mediaContextFactory.CreateDbContextAsync(ct);
                    outputDirectory = await ResolveOutputDirectoryAsync(job, mediaContext, ct);
                }

                string? tempDir = EncoderTempDirectory.MirroredPath(outputDirectory);
                if (tempDir is not null)
                    owned.Add(tempDir);
            }
        }
        finally
        {
            if (mediaContext is not null)
                await mediaContext.DisposeAsync();
        }

        return owned;
    }

    private static async Task<string?> ResolveOutputDirectoryAsync(
        VideoEncodeJob job,
        MediaContext mediaContext,
        CancellationToken ct
    )
    {
        Folder? folder = await mediaContext
            .Folders.AsNoTracking()
            .Include(f => f.FolderLibraries)
                .ThenInclude(fl => fl.Library)
            .FirstOrDefaultAsync(f => f.Id == job.FolderId, ct);
        if (folder is null)
            return null;

        job.LoggerFactory = NullLoggerFactory.Instance;
        VideoEncodeJob.FileMetadata metadata = await job.GetFileMetaData(folder, mediaContext);
        return metadata.Success ? metadata.Path : null;
    }
}
