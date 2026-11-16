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

using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Jobs;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Progress;
using NoMercy.Storage;

namespace NoMercy.Encoder.Strategies;

/// <summary>
/// Shared single-pass wrapper: delegates encoding to <see cref="IEncoder"/>
/// and provides crash-path partial-output sweep so the destination directory
/// is not left in a half-written state after a non-cancel encode failure.
///
/// On cancellation the checkpoint is NOT written (user-cancelled encodes are
/// not resumable). On a crash failure any segments already published to the
/// destination are swept, keeping the dir clean for a future retry.
/// The crash checkpoint written by ExecuteStage is left intact so orphan
/// recovery can re-queue with resume-from-keyframe.
/// </summary>
public abstract class SinglePassStrategyBase(IEncoder encoder, ILogger logger, IStorage storage)
    : IEncodingStrategy
{
    public abstract OutputFormat Format { get; }
    public EncodeMode EncodeMode => EncodeMode.SinglePass;

    public virtual DecomposedTask[] Decompose(OutputPlan plan, string groupTag) =>
        [IEncodingStrategy.WholeTask(groupTag)];

    public async Task<EncodingResult> EncodeAsync(
        EncodingRequest request,
        IProgressObserver? progress,
        CancellationToken ct
    )
    {
        IStorage effectiveStorage = request.DestinationStorage ?? request.SourceStorage ?? storage;

        // Per-task encodes (TaskFilter set on EncodingOptions) write into a
        // SHARED output directory alongside every other decomposed task in
        // the same coordinator group — see EncodingOrchestrator's isPerTaskRun
        // comment. Sweeping that directory here would delete sibling tasks'
        // segments the moment any one of them fails or is cancelled; the
        // coordinator owns cleanup of the whole group once every task in it
        // has reported, so per-task runs skip the sweep entirely.
        bool isPerTaskRun =
            request.Options?.TaskFilter is { } filter && filter.Kind != EncodeTaskKind.Whole;

        try
        {
            EncodingResult result = await encoder.EncodeAsync(request, progress, ct);

            if (!result.Success && !isPerTaskRun && !request.HostStopping.IsCancellationRequested)
            {
                DeletePartialOutput(
                    request.OutputDirectory,
                    effectiveStorage,
                    preserveCheckpoint: true
                );
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            if (!isPerTaskRun && !request.HostStopping.IsCancellationRequested)
            {
                DeletePartialOutput(
                    request.OutputDirectory,
                    effectiveStorage,
                    preserveCheckpoint: false
                );
            }
            throw;
        }
    }

    private void DeletePartialOutput(string outputDirectory, IStorage stor, bool preserveCheckpoint)
    {
        try
        {
            if (!stor.Exists(outputDirectory))
                return;

            foreach (
                StorageEntry entry in stor.List(outputDirectory, "*", recursive: true)
                    .Where(entry => !entry.IsDirectory)
                    .Where(entry =>
                        !preserveCheckpoint
                        || !Path.GetFileName(entry.Path)
                            .Equals(
                                CheckpointFileNames.FileName,
                                StringComparison.OrdinalIgnoreCase
                            )
                    )
            )
            {
                try
                {
                    stor.Delete(entry.Path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to delete partial output file {File} after crash",
                        entry.Path
                    );
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to enumerate partial output for deletion after crash in {OutputDirectory}",
                outputDirectory
            );
        }
    }
}
