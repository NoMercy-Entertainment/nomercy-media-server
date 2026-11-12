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
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Jobs;
using NoMercy.Encoder.Progress;
using NoMercy.Storage;

namespace NoMercy.Encoder.Pipeline.Stages;

public record ExecuteInput(
    FfmpegCommand[] Commands,
    TimeSpan InputDuration,
    IProgressObserver? Progress = null
);

public class ExecuteStage(
    IFfmpegExecutor executor,
    ICheckpointStore checkpointStore,
    ILogger<ExecuteStage> logger
) : IPipelineStage<ExecuteInput, ExecutionResult[]>, IExecutionStage
{
    public string Name => "Execute";

    public async Task<StageResult> ExecuteAsync(
        ExecuteInput input,
        EncodingContext context,
        CancellationToken ct
    )
    {
        logger.LogInformation(
            "[{CorrelationId}] Executing {Count} command(s)",
            context.CorrelationId,
            input.Commands.Length
        );

        List<ExecutionResult> results = [];
        long lastProgressMs = 0;

        try
        {
            for (int i = 0; i < input.Commands.Length; i++)
            {
                FfmpegCommand cmd = input.Commands[i];

                // Only the main encode command (index 0) reports progress.
                // Post-processing commands (subtitles, fonts) are short-lived.
                Action<EncodingProgress>? onProgress = null;
                if (i == 0 && input.Progress is not null)
                {
                    onProgress = progress =>
                    {
                        lastProgressMs = (long)(progress.CurrentTimeSeconds * 1000);
                        input.Progress.OnProgress(progress);
                    };
                }
                else if (i == 0)
                {
                    onProgress = progress =>
                    {
                        lastProgressMs = (long)(progress.CurrentTimeSeconds * 1000);
                    };
                }

                ExecutionResult result = await executor.ExecuteAsync(
                    cmd,
                    input.InputDuration,
                    onProgress: onProgress,
                    correlationId: context.CorrelationId,
                    ct: ct
                );

                results.Add(result);

                if (!result.Success)
                {
                    // Command 0 is the main encode — must succeed.
                    // Subsequent commands (subtitle extraction, font extraction, thumbnails)
                    // are post-processing — failures are logged but not fatal.
                    if (i == 0)
                    {
                        EncodingError error =
                            result.Error
                            ?? new EncodingError(
                                EncodingErrorKind.ProcessCrashed,
                                "FFmpeg exited with non-zero code",
                                result.StdErr,
                                Name,
                                true
                            );

                        await WriteCrashCheckpointAsync(context, lastProgressMs, result.StdErr, ct);

                        return new StageFailure(error);
                    }

                    logger.LogWarning(
                        "[{CorrelationId}] Post-process command {Index} failed (non-fatal; its subtitle, font or sprite files may be missing): exit={ExitCode} stderr={StdErr}",
                        context.CorrelationId,
                        i,
                        result.ExitCode,
                        TailStderr(result.StdErr)
                    );
                }
            }

            return new StageSuccess<ExecutionResult[]>(results.ToArray());
        }
        finally
        {
            CleanupDrmKeyArtifacts(input.Commands, context.CorrelationId);
        }
    }

    /// <summary>
    /// Aes128HlsDrmProcessor writes drm.key/drm_keyinfo.txt to a per-encode
    /// directory under StoragePaths.TempRoot (never OutputDirectory — that
    /// directory is published to the served destination). Ffmpeg has now
    /// either consumed that -hls_key_info_file or failed trying, so the temp
    /// directory can go. Only ever deletes paths under TempRoot — a defensive
    /// scope check in case a future DRM processor writes artifacts elsewhere.
    /// </summary>
    private void CleanupDrmKeyArtifacts(FfmpegCommand[] commands, string correlationId)
    {
        foreach (FfmpegCommand cmd in commands)
        {
            int idx = Array.IndexOf(cmd.Arguments, "-hls_key_info_file");
            if (idx < 0 || idx + 1 >= cmd.Arguments.Length)
                continue;

            string keyInfoPath = cmd.Arguments[idx + 1];
            string? tempDir = Path.GetDirectoryName(keyInfoPath);
            if (string.IsNullOrEmpty(tempDir))
                continue;

            string fullTempDir = Path.GetFullPath(tempDir);
            string fullTempRoot = Path.GetFullPath(StoragePaths.TempRoot);
            if (!fullTempDir.StartsWith(fullTempRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (Directory.Exists(fullTempDir))
                    Directory.Delete(fullTempDir, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[{CorrelationId}] Failed to delete DRM key temp directory {Directory}",
                    correlationId,
                    fullTempDir
                );
            }
        }
    }

    private async Task WriteCrashCheckpointAsync(
        EncodingContext context,
        long lastProgressMs,
        string stderrTail,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(context.OutputDirectory))
            return;

        try
        {
            JobCheckpoint checkpoint = new(
                JobId: context.CorrelationId,
                InputPath: context.InputPath ?? string.Empty,
                OutputDirectory: context.OutputDirectory,
                CompletedGroupIndices: [],
                LastUpdated: DateTime.UtcNow,
                LastProgressMs: lastProgressMs,
                LastFfmpegStderrTail: TailStderr(stderrTail),
                FailedAt: DateTime.UtcNow
            );

            await checkpointStore.SaveAsync(checkpoint, ct);

            logger.LogWarning(
                "[{CorrelationId}] Crash checkpoint saved at {OutputDirectory} — LastProgressMs={Ms}",
                context.CorrelationId,
                context.OutputDirectory,
                lastProgressMs
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[{CorrelationId}] Failed to save crash checkpoint",
                context.CorrelationId
            );
        }
    }

    private static string TailStderr(string stderr)
    {
        const int maxBytes = 16 * 1024;
        if (string.IsNullOrEmpty(stderr) || stderr.Length <= maxBytes)
            return stderr;

        return stderr[^maxBytes..];
    }
}
