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

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Strategies;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Encoder.Orchestration;

public class EncodingOrchestrator(
    IStrategyResolver resolver,
    IStorage storage,
    IEncoder encoder,
    ILogger<EncodingOrchestrator> logger,
    INvencSessionCap? nvencCap = null
) : IEncodingOrchestrator
{
    public async Task<EncodingResult> EncodeAsync(
        EncodingRequest request,
        IProgressObserver? progress = null,
        CancellationToken ct = default
    )
    {
        // Dispatch-time NVENC session cap enforcement (Phase 3.7).
        // Fires before the strategy is resolved so a saturated GPU returns 409
        // immediately rather than failing mid-encode inside ffmpeg.
        // Only applies when the profile requests hardware encoding.
        bool wantsGpu =
            request.Profile.HardwarePreference
            is HardwarePreference.PreferHardware
                or HardwarePreference.ForceHardware;

        if (nvencCap is not null && wantsGpu)
        {
            string gpuName = request.Profile.Name;

            nvencCap.EnforceForGpuEncode(gpuName: gpuName, requiresGpu: true);
        }

        OutputFormat profileFormat = PlanStageHelpers.ContainerToOutputFormat(
            container: request.Profile.Container
        );

        IEncodingStrategy? strategy = resolver.Resolve(format: profileFormat, mode: request.Profile.EncodeMode);

        if (strategy is null)
        {
            EncodingError error = new(
                Kind: EncodingErrorKind.Unknown,
                Message: $"No strategy registered for {profileFormat} / "
                    + $"{request.Profile.EncodeMode}. Register an IEncodingStrategy implementation "
                    + "for this combination.",
                FfmpegStderr: null,
                StageName: "Orchestrator",
                Recoverable: false
            );
            progress?.OnError(error: error);
            EncoderErrorShape errorShape = new(
                Id: EncoderRuleId.EncoderInitFailed,
                Message: error.Message,
                Suggestion: "Register an IEncodingStrategy for this format + mode combination.",
                Details: null
            );
            return new(
                Success: false,
                OutputPath: string.Empty,
                Duration: TimeSpan.Zero,
                Error: error,
                Metrics: new(OutputSizeBytes: 0, AverageSpeed: 0, AverageFps: 0, EncoderUsed: string.Empty, GpuUsed: null)
            )
            {
                Status = "failed",
                EnrichedError = errorShape,
            };
        }

        logger.LogInformation(
            message: "Dispatching to {Strategy} ({Format}/{Mode}) for {Input}", args: [strategy.GetType().Name, strategy.Format, strategy.EncodeMode, request.InputPath]
        );

        // Resolve per-request storages. Jobs supply per-folder IStorage instances
        // that enforce path guards and backend routing. Fall back to the DI singleton
        // for callers that do not set per-folder storage (live transcoder, tests, etc.).
        // Cross-backend: source staged via AcquireLocalPathAsync; remote destination
        // gets a local temp working dir, then artifacts upload via destinationStorage.
        IStorage sourceStorage = request.SourceStorage ?? storage;
        IStorage destinationStorage =
            request.DestinationStorage ?? request.SourceStorage ?? storage;

        Stopwatch wall = Stopwatch.StartNew();

        try
        {
            // Measure staging (remote source -> local temp) separately from the
            // encoding below. On a LocalStorage source this is a no-op path return;
            // on a remote source it is a full download that today runs BEFORE the
            // first frame is encoded. Logging staging-vs.-encode tells us whether
            // overlapping the two (streaming ffmpeg's input) is worth the work.
            Stopwatch stagingWatch = Stopwatch.StartNew();
            await using LocalPathLease lease = await sourceStorage.AcquireLocalPathAsync(
                path: request.InputPath,
                ct: ct
            );
            stagingWatch.Stop();
            LogStaging(inputPath: request.InputPath, sourceStorage: sourceStorage, leasePath: lease.Path, elapsed: stagingWatch.Elapsed);

            EncodingResult result;

            Directory.CreateDirectory(path: StoragePaths.TranscodeRoot);
            // Mirror the final OutputDirectory structure under the transcoding root
            // so working files land at e.g.,
            //   cache/encoder/<Show>.(<Year>)/<Show>.SixEx/
            // instead of an opaque nomercy-enc-<ulid> dir. Easy to inspect, easy
            // to wipe per show. OutputDirectory is a relative path from the
            // destination storage root; treat / and \ as portable separators.
            string rawOutputDirectory = request.OutputDirectory ?? string.Empty;
            // Reject rootedness (in ANY OS style, not just the host's own)
            // before the '/' trim below ever runs: Trim('/') on a genuinely
            // Unix-rooted path ("/etc/passwd") strips the very leading slash
            // that marks it absolute, turning it into a "relative" segment
            // that then re-joins right back under TranscodeRoot below —
            // silently defeating the containment check on Linux for a path
            // that Windows' own Path.Combine would have already discarded
            // the root for. Checking here, cross-platform, up front, closes
            // that gap on every platform instead of relying on the host OS
            // to recognize its own rootedness notation.
            if (StoragePathGuard.IsRootedAnyStyle(path: rawOutputDirectory))
                throw new InvalidOperationException(
                    message: $"Encoder OutputDirectory '{rawOutputDirectory}' is a rooted path. "
                             + "OutputDirectory must resolve to a path under the transcode root — "
                             + "check for a rooted path or '..' traversal."
                );
            string relativeOutputPath = rawOutputDirectory.Replace(oldChar: '\\', newChar: '/').Trim(trimChar: '/');
            string tempDir = string.IsNullOrEmpty(value: relativeOutputPath)
                ? Path.Combine(path1: StoragePaths.TranscodeRoot, path2: $"nomercy-enc-{Ulid.NewUlid()}")
                : Path.Combine(
                    path1: StoragePaths.TranscodeRoot,
                    path2: relativeOutputPath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar)
                );
            // request.OutputDirectory is documented as relative, but nothing
            // structurally enforces that beyond the rootedness check above: a
            // "../.." traversal escapes TranscodeRoot just as easily as a
            // rooted path does. Reject anything that resolves outside
            // TranscodeRoot before it's ever created or (on the cleanup path
            // below) recursively deleted.
            tempDir = EnsureWithinTranscodeRoot(candidateTempDir: tempDir);
            // CreateDirectory is idempotent — returns the existing DirectoryInfo
            // when the path already exists. FFmpeg's -y overwrites any stale
            // segments from a prior run, so wiping isn't necessary, and a
            // recursive Delete races against Process.Start's working-directory
            // resolution on Windows (intermittently leaves the dir gone when
            // ffmpeg.exe boots a moment later).
            try
            {
                Directory.CreateDirectory(path: tempDir);
            }
            catch (Exception ex)
            {
                logger.LogError(exception: ex, message: "Failed to create encoder temp dir {TempDir}", args: tempDir);
                throw;
            }

            if (!Directory.Exists(path: tempDir))
            {
                logger.LogWarning(
                    message: "Encoder temp dir {TempDir} not present after CreateDirectory — recreating",
                    args: tempDir
                );
                Directory.CreateDirectory(path: tempDir);
            }

            try
            {
                // Strategy always writes to a local temp dir, so ffmpeg has a
                // real filesystem path regardless of backend type. SourceStorage
                // also swaps to the DI LocalStorage so stages can probe the
                // staged input via FileExists/OpenRead. After encoding the
                // publish loop moves or copies artifacts to the real destination.
                EncodingRequest stagedRequest = request with
                {
                    InputPath = lease.Path,
                    OutputDirectory = tempDir,
                    SourceStorage = storage,
                    DestinationStorage = storage,
                    // Both paths above are about to stop existing — the lease is
                    // released, and the temp dir is published and deleted. Carry
                    // what they stand in for, or the manifest and reconstruction
                    // written downstream describe only the scaffolding.
                    OriginalInputPath = request.InputPath,
                    OriginalOutputDirectory = request.OutputDirectory,
                };

                result = await strategy.EncodeAsync(request: stagedRequest, progress: progress, ct: ct);
                wall.Stop();

                // Sits next to the staging line so the log shows the split
                // directly: how much of the job was transfer vs. actual encode.
                // wall spans staging+encode (it feeds total-duration stats below),
                // so subtract staging to report encode-only here.
                double encodeOnlySeconds = Math.Max(
                    val1: 0,
                    val2: wall.Elapsed.TotalSeconds - stagingWatch.Elapsed.TotalSeconds
                );
                logger.LogInformation(
                    message: "Encode finished in {Seconds:F1}s (excludes {Staging:F1}s staging) [{Input}]", args: [encodeOnlySeconds, stagingWatch.Elapsed.TotalSeconds, request.InputPath]
                );

                // Per-task encodes (TaskFilter set on EncodingOptions) write
                // to a SHARED tempDir derived from the relative OutputDirectory.
                // All decomposed tasks for a single coordinator end up pointing
                // at the same cache path. If each task ran the publish loop, it
                // would race every other task in the group: list ALL files
                // (including in-progress writes from other tasks), File.Move
                // them, then Directory.Delete the tempDir — wiping outputs the
                // peers haven't finished writing yet. The coordinator
                // (VideoEncodeJob.HandleFinalizeAsync) publishes once after
                // every task in the group completes, so per-task runs skip
                // both the publish loop and the cleanup finally.
                // A bundled Whole task (dispatch-time smart-combine: one ffmpeg
                // covering N stream-tasks) IS the only execution — it must
                // publish + cleanup like a plain Whole strategy run. Only
                // per-stream slices (Video/Audio/Subtitle/Thumbnails) defer
                // these to the coordinator's FinalizeOnly pass.
                bool isPerTaskRun =
                    request.Options?.TaskFilter is { } filter
                    && filter.Kind != EncodeTaskKind.Whole;

                if (result.Success && !isPerTaskRun)
                {
                    await PublishTempDirAsync(
                        tempDir: tempDir,
                        outputDirectory: request.OutputDirectory ?? string.Empty,
                        destinationStorage: destinationStorage,
                        progress: progress,
                        ct: ct
                    );
                }
            }
            finally
            {
                // Per-task runs leave files for the coordinator to publish +
                // sweep — see isPerTaskRun comment above the publish loop.
                bool isPerTaskRun =
                    request.Options?.TaskFilter is { } filter
                    && filter.Kind != EncodeTaskKind.Whole;
                if (!isPerTaskRun)
                {
                    try
                    {
                        // Re-validated immediately before the recursive delete —
                        // tempDir is already containment-checked above, but a
                        // rooted-path or traversal escape here would otherwise
                        // recursively delete whatever directory it resolved to.
                        Directory.Delete(path: EnsureWithinTranscodeRoot(candidateTempDir: tempDir), recursive: true);
                    }
                    catch (Exception cleanEx)
                    {
                        logger.LogWarning(
                            exception: cleanEx,
                            message: "Could not clean up temp encode dir {TempDir}",
                            args: tempDir
                        );
                    }
                }
            }

            if (!result.Success)
            {
                return result with
                {
                    Status = "failed",
                    EnrichedError =
                        result.EnrichedError
                        ?? new EncoderErrorShape(
                            Id: EncoderRuleId.EncoderInitFailed,
                            Message: result.Error?.Message ?? "Encode failed with no details.",
                            Suggestion: null,
                            Details: null
                        ),
                };
            }

            IReadOnlyList<OutputArtifact> artifacts = await BuildArtifactsAsync(
                outputDirectory: request.OutputDirectory ?? string.Empty,
                stor: destinationStorage,
                ct: ct
            );

            // OutputBytes counts everything on disk (segments + sidecars +
            // thumbnails); artifacts are stream-level only. Walk separately.
            long outputBytes = await SumOutputBytesAsync(
                outputDirectory: request.OutputDirectory ?? string.Empty,
                stor: destinationStorage,
                ct: ct
            );
            long sourceBytes = await GetSourceBytesAsync(inputPath: request.InputPath, stor: sourceStorage, ct: ct);
            double durationSec = wall.Elapsed.TotalSeconds;
            double avgFps = result.Metrics?.AverageFps ?? 0;
            int bitrateKbps = durationSec > 0 ? (int)(outputBytes * 8L / (durationSec * 1000)) : 0;

            EncodeStats stats = new(
                DurationSeconds: durationSec,
                AvgFps: avgFps,
                OutputBitrateKbps: bitrateKbps,
                SourceBytes: sourceBytes,
                OutputBytes: outputBytes
            );

            return result with
            {
                Status = "success",
                Artifacts = artifacts,
                Stats = stats,
            };
        }
        catch (OperationCanceledException)
        {
            wall.Stop();
            logger.LogWarning(
                message: "Encode cancelled for {Input} after {Elapsed:F1}s", args: [request.InputPath, wall.Elapsed.TotalSeconds]
            );
            return new(
                Success: false,
                OutputPath: string.Empty,
                Duration: wall.Elapsed,
                Error: null,
                Metrics: null
            )
            {
                Status = "cancelled",
            };
        }
        catch (EncoderRuntimeException rex)
        {
            wall.Stop();
            logger.LogError(
                exception: rex,
                message: "Encoder runtime failure for {Input}: [{Id}] {Message}", args: [request.InputPath, rex.Shape.Id, rex.Shape.Message]
            );
            EncodingError error = new(
                Kind: EncodingErrorKind.Unknown,
                Message: rex.Shape.Message,
                FfmpegStderr: null,
                StageName: "Orchestrator",
                Recoverable: false
            );
            progress?.OnError(error: error);
            return new(
                Success: false,
                OutputPath: string.Empty,
                Duration: wall.Elapsed,
                Error: error,
                Metrics: null
            )
            {
                Status = "failed",
                EnrichedError = rex.Shape,
            };
        }
        catch (Exception ex)
        {
            wall.Stop();
            logger.LogError(exception: ex, message: "Unexpected error encoding {Input}", args: request.InputPath);
            EncodingError error = new(
                Kind: EncodingErrorKind.Unknown,
                Message: ex.Message,
                FfmpegStderr: null,
                StageName: "Orchestrator",
                Recoverable: false
            );
            progress?.OnError(error: error);
            EncoderErrorShape errorShape = new(
                Id: EncoderRuleId.EncoderInitFailed,
                Message: ex.Message,
                Suggestion: null,
                Details: null
            );
            return new(
                Success: false,
                OutputPath: string.Empty,
                Duration: wall.Elapsed,
                Error: error,
                Metrics: null
            )
            {
                Status = "failed",
                EnrichedError = errorShape,
            };
        }
    }

    // Logs how long staging the source to a local temp file took, and the
    // effective throughput. A LocalStorage source returns its own path unchanged
    // (no download) — detected by the lease path matching the input — so nothing
    // is logged for the local case. For remote sources this is the transfer that
    // currently blocks the encoding from starting; the number here is what a
    // streaming (overlapped) input would reclaim.
    private void LogStaging(
        string inputPath,
        IStorage sourceStorage,
        string leasePath,
        TimeSpan elapsed
    )
    {
        bool staged = !string.Equals(
            a: Path.GetFullPath(path: leasePath),
            b: SafeFullPath(path: inputPath),
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
        if (!staged)
            return;

        long bytes = 0;
        try
        {
            bytes = new FileInfo(fileName: leasePath).Length;
        }
        catch
        {
            // size is best-effort — the timing is the point
        }

        double seconds = elapsed.TotalSeconds;
        double megaBytes = seconds > 0 ? bytes / 1_000_000.0 / seconds : 0;
        double megabits = megaBytes * 8; // megabits per second
        logger.LogInformation(
            message: "Staged source for encoding: {Backend} {MB:F0} MB in {Seconds:F1}s "
                     + "({megaBytes:F0} MB/s, {megabits:F0} Mbps) before ffmpeg start [{Input}]", 
                sourceStorage.Driver.BackendLabel, 
            bytes / 1_000_000.0, 
            seconds, 
            megaBytes, 
            megabits, 
            inputPath
        );
    }

    /// <summary>
    /// Resolves <paramref name="candidateTempDir"/> to its canonical full path
    /// and throws unless it is <see cref="StoragePaths.TranscodeRoot"/> itself
    /// or a descendant of it. The candidate is built from
    /// <c>request.OutputDirectory</c>, which is documented as storage-relative
    /// but not structurally enforced at this boundary — a rooted path or a
    /// "." traversal would otherwise let <see cref="Directory.CreateDirectory"/>
    /// and the recursive <see cref="Directory.Delete"/> below operate on an
    /// arbitrary filesystem location instead of the transcoding cache.
    /// </summary>
    private static string EnsureWithinTranscodeRoot(string candidateTempDir)
    {
        string normalizedRoot = Path.GetFullPath(path: StoragePaths.TranscodeRoot);
        string normalizedTempDir = Path.GetFullPath(path: candidateTempDir);

        string rootWithSeparator = normalizedRoot.EndsWith(value: Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        bool isRootItself = string.Equals(
            a: normalizedTempDir,
            b: normalizedRoot,
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
        bool isUnderRoot = normalizedTempDir.StartsWith(
            value: rootWithSeparator,
            comparisonType: StringComparison.OrdinalIgnoreCase
        );

        if (!isRootItself && !isUnderRoot)
        {
            throw new InvalidOperationException(
                message: $"Encoder temp dir '{normalizedTempDir}' escapes the transcode root "
                         + $"'{normalizedRoot}'. OutputDirectory must resolve to a path under "
                         + "the transcode root — check for a rooted path or '..' traversal."
            );
        }

        return normalizedTempDir;
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path: path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// Run a single decomposed task. Injects a <see cref="DecomposedTask"/>
    /// into the request's <see cref="EncodingOptions.TaskFilter"/> so that
    /// <see cref="BuildStage"/> emits only the commands relevant to this task.
    /// All other pipeline stages run normally — the task still gets analyzed,
    /// validated, and planned, but only its slice is built and executed.
    /// </summary>
    public Task<EncodingResult> EncodeAsync(
        EncodingRequest request,
        DecomposedTask task,
        IProgressObserver? progress = null,
        CancellationToken ct = default
    )
    {
        EncodingOptions filteredOptions = (request.Options ?? new EncodingOptions()) with
        {
            TaskFilter = task,
        };
        return EncodeAsync(request: request with { Options = filteredOptions }, progress: progress, ct: ct);
    }

    public async Task<DecomposedTask[]> DecomposeAsync(
        EncodingRequest request,
        string groupTag,
        CancellationToken ct = default
    )
    {
        (_, DecomposedTask[] tasks) = await DecomposeCoreAsync(request: request, groupTag: groupTag, ct: ct)
            .ConfigureAwait(continueOnCapturedContext: false);
        return tasks;
    }

    public async Task<(OutputPlan? Plan, DecomposedTask[] Tasks)> DecomposeWithPlanAsync(
        EncodingRequest request,
        string groupTag,
        CancellationToken ct = default
    ) => await DecomposeCoreAsync(request: request, groupTag: groupTag, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

    /// <summary>
    /// Shared implementation behind <see cref="DecomposeAsync"/> and
    /// <see cref="DecomposeWithPlanAsync"/> — plans once and hands the
    /// resolved <see cref="OutputPlan"/> back alongside the decomposed
    /// tasks so a caller that needs both (the decode-aware bundler) does
    /// not have to re-plan, which would re-stage/re-probe a remote source.
    /// <see cref="OutputPlan"/> is null exactly when the strategy or the
    /// plan itself could not be resolved — both cases already degrade to
    /// the single <see cref="EncodeTaskKind.Whole"/> fallback task, which
    /// never reaches the decode-aware bundler.
    /// </summary>
    private async Task<(OutputPlan? Plan, DecomposedTask[] Tasks)> DecomposeCoreAsync(
        EncodingRequest request,
        string groupTag,
        CancellationToken ct
    )
    {
        OutputFormat profileFormat = PlanStageHelpers.ContainerToOutputFormat(
            container: request.Profile.Container
        );

        IEncodingStrategy? strategy = resolver.Resolve(format: profileFormat, mode: request.Profile.EncodeMode);

        if (strategy is null)
            return (null, [IEncodingStrategy.WholeTask(groupTag: groupTag)]);

        // Stage the input file locally so PlanAsync can probe it via ffprobe
        // regardless of the source storage backend.
        await using LocalPathLease lease = await (
            request.SourceStorage ?? storage
        ).AcquireLocalPathAsync(path: request.InputPath, ct: ct);

        EncodingRequest stagedRequest = request with
        {
            InputPath = lease.Path,
            SourceStorage = storage,
            DestinationStorage = storage,
            OriginalInputPath = request.InputPath,
        };

        OutputPlan? plan = await encoder.PlanAsync(request: stagedRequest, ct: ct);

        if (plan is null)
            return (null, [IEncodingStrategy.WholeTask(groupTag: groupTag)]);

        return (plan, strategy.Decompose(plan: plan, groupTag: groupTag));
    }

    public async Task<OutputPlan?> PlanMergedAsync(
        IReadOnlyList<EncodingRequest> requests,
        CancellationToken ct = default
    )
    {
        if (requests.Count == 0)
            return null;

        List<OutputPlan> plans = new(capacity: requests.Count);

        foreach (EncodingRequest request in requests)
        {
            await using LocalPathLease lease = await (
                request.SourceStorage ?? storage
            ).AcquireLocalPathAsync(path: request.InputPath, ct: ct);

            EncodingRequest stagedRequest = request with
            {
                InputPath = lease.Path,
                SourceStorage = storage,
                DestinationStorage = storage,
                OriginalInputPath = request.InputPath,
            };

            OutputPlan? plan = await encoder.PlanAsync(request: stagedRequest, ct: ct);
            if (plan is null)
            {
                logger.LogWarning(
                    message: "[EncodingOrchestrator] PlanMergedAsync: preset '{Name}' failed to plan — cannot merge.",
                    args: request.Profile.Name
                );
                return null;
            }

            if (plans.Count > 0 && plan.Format != plans[index: 0].Format)
            {
                logger.LogWarning(
                    message: "[EncodingOrchestrator] PlanMergedAsync: preset '{Name}' resolves to {Format}, "
                             + "incompatible with the primary preset's {PrimaryFormat} — cannot merge.", args: [request.Profile.Name, plan.Format, plans[index: 0].Format]
                );
                return null;
            }

            plans.Add(item: plan);
        }

        return OutputPlanMerger.Merge(plans: plans);
    }

    public async Task<DecomposedTask[]> DecomposeMergedAsync(
        IReadOnlyList<EncodingRequest> requests,
        string groupTag,
        CancellationToken ct = default
    )
    {
        (_, DecomposedTask[] tasks) = await DecomposeMergedWithPlanAsync(requests: requests, groupTag: groupTag, ct: ct)
            .ConfigureAwait(continueOnCapturedContext: false);
        return tasks;
    }

    /// <summary>
    /// Plan is null exactly when planning failed or no strategy resolved —
    /// both cases degrade <paramref name="requests"/>[0]'s decompose to the
    /// single <see cref="EncodeTaskKind.Whole"/> fallback, which the
    /// coordinator runs inline and never hands to the decode-aware bundler,
    /// so a null Plan never reaches <c>DecodeAwareBundlePlanner</c>.
    /// </summary>
    public async Task<(OutputPlan? Plan, DecomposedTask[] Tasks)> DecomposeMergedWithPlanAsync(
        IReadOnlyList<EncodingRequest> requests,
        string groupTag,
        CancellationToken ct = default
    )
    {
        if (requests.Count == 0)
            throw new MergedEncodingIncompatibleException(
                message: "DecomposeMergedAsync requires at least one request."
            );

        // Merge of one is exactly today's single-preset path — no strategy
        // resolution differences, no plan-merge machinery involved.
        if (requests.Count == 1)
            return await DecomposeCoreAsync(request: requests[index: 0], groupTag: groupTag, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

        EncodingRequest primaryRequest = requests[index: 0];

        if (
            requests.Any(predicate: request => request.Profile.EncodeMode != primaryRequest.Profile.EncodeMode)
        )
            throw new MergedEncodingIncompatibleException(
                message: "Cannot merge presets with different encode modes (single-pass vs two-pass)."
            );

        OutputPlan? mergedPlan = await PlanMergedAsync(requests: requests, ct: ct);
        if (mergedPlan is null)
            throw new MergedEncodingIncompatibleException(
                message: "One or more presets failed to plan, or their plans resolve to incompatible "
                         + "output formats — see the preceding warning for which preset."
            );

        OutputFormat profileFormat = PlanStageHelpers.ContainerToOutputFormat(
            container: primaryRequest.Profile.Container
        );
        IEncodingStrategy? strategy = resolver.Resolve(
            format: profileFormat,
            mode: primaryRequest.Profile.EncodeMode
        );

        if (strategy is null)
            throw new MergedEncodingIncompatibleException(
                message: $"No strategy registered for {profileFormat} / {primaryRequest.Profile.EncodeMode}."
            );

        return (mergedPlan, strategy.Decompose(plan: mergedPlan, groupTag: groupTag));
    }

    // Tells the library file watcher the publish is the server's own output.
    private static IDisposable? HoldDestination(IStorage dest, string outputDirectory)
    {
        if (dest is not LocalStorage)
            return null;

        try
        {
            return ServerWriteLedger.Shared.Hold(folder: dest.GetFullPath(path: outputDirectory));
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves every file under <paramref name="tempDir"/> to its mirrored path
    /// under <paramref name="outputDirectory"/> on <paramref name="destinationStorage"/>.
    /// Uses an atomic <c>File.Move</c> on same-volume local destinations, falls
    /// back to a copy stream for remote backends or cross-volume cases. Parent
    /// directories are created on demand because NFS <c>creat</c> rejects writes
    /// when the enclosing directory is missing.
    /// </summary>
    internal async Task PublishTempDirAsync(
        string tempDir,
        string outputDirectory,
        IStorage destinationStorage,
        IProgressObserver? progress,
        CancellationToken ct
    )
    {
        bool isLocalDest = destinationStorage is LocalStorage;
        bool sameVolume = false;
        using IDisposable? hold = HoldDestination(dest: destinationStorage, outputDirectory: outputDirectory);

        if (isLocalDest)
        {
            string? firstFile = Directory
                .EnumerateFiles(path: tempDir, searchPattern: "*", searchOption: SearchOption.AllDirectories)
                .FirstOrDefault();
            if (firstFile is not null)
            {
                string probeRel = Path.GetRelativePath(relativeTo: tempDir, path: firstFile).Replace(oldChar: '\\', newChar: '/');
                string probeDest = string.Join(
                    separator: '/', value: [outputDirectory.TrimEnd(trimChar: '/'), probeRel.TrimStart(trimChar: '/')]
                );
                try
                {
                    string destAbs = destinationStorage.GetFullPath(path: probeDest);
                    sameVolume = string.Equals(
                        a: Path.GetPathRoot(path: firstFile),
                        b: Path.GetPathRoot(path: destAbs),
                        comparisonType: StringComparison.OrdinalIgnoreCase
                    );
                }
                catch (NotSupportedException) { }
            }
        }

        string stageName = ResolvePublishStageName(dest: destinationStorage, sameVolume: sameVolume);
        progress?.OnStageStarted(stageName: stageName);
        Stopwatch stageWatch = Stopwatch.StartNew();
        try
        {
            foreach (
                string localFile in Directory.EnumerateFiles(
                    path: tempDir,
                    searchPattern: "*",
                    searchOption: SearchOption.AllDirectories
                )
            )
            {
                string rel = Path.GetRelativePath(relativeTo: tempDir, path: localFile).Replace(oldChar: '\\', newChar: '/');
                string remoteDest = string.Join(
                    separator: '/', value: [outputDirectory.TrimEnd(trimChar: '/'), rel.TrimStart(trimChar: '/')]
                );

                if (sameVolume)
                {
                    bool moved = false;
                    try
                    {
                        string destAbs = destinationStorage.GetFullPath(path: remoteDest);
                        Directory.CreateDirectory(path: Path.GetDirectoryName(path: destAbs)!);
                        File.Move(sourceFileName: localFile, destFileName: destAbs, overwrite: true);
                        moved = true;
                    }
                    catch (IOException)
                    {
                        // Cross-volume race or permission edge-case —
                        // fall through to the copy path below.
                    }

                    if (moved)
                        continue;
                }

                string? remoteParent = destinationStorage.GetParent(path: remoteDest);
                if (!string.IsNullOrEmpty(value: remoteParent))
                    destinationStorage.CreateDirectory(path: remoteParent);

                // Publish through a sibling temp file, then swap into place. A
                // process kill mid-copy would otherwise leave a truncated file
                // at the real library path that looks complete to a player;
                // writing to <dest>.<token>.nmpart and renaming means an
                // interrupted publish leaves only the temp (safe to re-encode),
                // never a corrupt final. The rename is the same-directory, so it is
                // an atomic metadata swap on local/SMB and an object-level swap
                // on S3.
                string publishTemp = $"{remoteDest}.{Guid.NewGuid():N}.nmpart";
                try
                {
                    // Read through the local IStorage so the guard layer still
                    // applies — File.OpenRead would bypass the path allowlist
                    // that every other read path honors.
                    await using (Stream src = await storage.OpenReadAsync(path: localFile, ct: ct))
                    await using (
                        Stream dst = await destinationStorage.OpenWriteAsync(
                            path: publishTemp,
                            overwrite: true,
                            ct: ct
                        )
                    )
                    {
                        await src.CopyToAsync(destination: dst, cancellationToken: ct);
                    }

                    // MoveFile does not overwrite on local/SMB backends, so clear
                    // any prior artifact before the swap.
                    if (await destinationStorage.ExistsAsync(path: remoteDest, ct: ct))
                        await destinationStorage.DeleteAsync(path: remoteDest, ct: ct);

                    await destinationStorage.MoveAsync(from: publishTemp, to: remoteDest, ct: ct);
                }
                catch
                {
                    // Best-effort cleanup of the partial temp; never mask the
                    // original failure (including cancellation).
                    try
                    {
                        if (
                            await destinationStorage.ExistsAsync(
                                path: publishTemp,
                                ct: CancellationToken.None
                            )
                        )
                            await destinationStorage.DeleteAsync(
                                path: publishTemp,
                                ct: CancellationToken.None
                            );
                    }
                    catch
                    {
                        // ignore — a stale temp is harmless and swept later
                    }

                    throw;
                }
            }
            stageWatch.Stop();
            progress?.OnStageCompleted(stageName: stageName, duration: stageWatch.Elapsed);
        }
        catch (Exception)
        {
            stageWatch.Stop();
            throw;
        }
    }

    // Stream-level artifacts only — the master + variant playlists for HLS,
    // the mixed file itself for MP4 / MKV / audio outputs. Per-segment
    // hashing was costing ~57s on an SMB-mounted output dir for one HLS
    // encode (hundreds of .ts / .m4s / .vtt / sprite files); none of it
    // was consumed downstream. Sizes still come from the recursive walk,
    // so EncodeStats.OutputBytes stays accurate.
    private async Task<IReadOnlyList<OutputArtifact>> BuildArtifactsAsync(
        string outputDirectory,
        IStorage stor,
        CancellationToken ct
    )
    {
        List<OutputArtifact> artifacts = [];

        IAsyncEnumerable<StorageEntry>? entries;
        try
        {
            entries = stor.ListAsync(path: outputDirectory, pattern: null, recursive: true, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                exception: ex,
                message: "Could not list output directory {Dir} — no artifacts catalogued",
                args: outputDirectory
            );
            return artifacts;
        }

        if (entries is null)
        {
            return artifacts;
        }

        await foreach (StorageEntry entry in entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            if (!IsStreamLevelArtifact(path: entry.Path))
            {
                continue;
            }

            try
            {
                string hash = await stor.HashAsync(path: entry.Path, algorithm: "sha256", ct: ct);
                string mime = OutputArtifact.MimeFromPath(path: entry.Path);
                artifacts.Add(item: new(Path: entry.Path, SizeBytes: entry.SizeBytes, Sha256: hash, MediaType: mime));
            }
            catch (Exception ex)
            {
                logger.LogWarning(exception: ex, message: "Could not catalogue artifact {Path} — skipping", args: entry.Path);
            }
        }

        return artifacts;
    }

    private static bool IsStreamLevelArtifact(string path)
    {
        string ext = Path.GetExtension(path: path).ToLowerInvariant();
        string name = Path.GetFileName(path: path).ToLowerInvariant();

        // HLS playlists — master + per-variant. Segment files (.ts / .m4s)
        // and segment-wrapping subtitle playlists (subs_<lang>_<variant>.m3u8
        // sit next to .vtt segments) are the SAME structural fingerprint as
        // their variant playlist, so skip the segment files but keep all
        // .m3u8 — playlists are KB-sized text, hashing is free.
        if (ext == ".m3u8")
            return true;

        // Single-file muxed outputs.
        if (ext is ".mp4" or ".mkv" or ".webm" or ".flac" or ".mp3" or ".ogg" or ".opus")
            return true;

        // ASS / SRT sidecars when the profile preserves them as direct files.
        if (ext is ".ass" or ".srt")
            return true;

        // Everything else (segments, fmp4 init, thumbnails, sprite vtt,
        // per-segment .vtt) is auxiliary and skipped.
        return false;
    }

    private async Task<long> GetSourceBytesAsync(
        string inputPath,
        IStorage stor,
        CancellationToken ct
    )
    {
        try
        {
            return await stor.SizeAsync(path: inputPath, ct: ct);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // Plain size walk — no hashing, no extra I/O beyond directory metadata.
    private async Task<long> SumOutputBytesAsync(
        string outputDirectory,
        IStorage stor,
        CancellationToken ct
    )
    {
        long total = 0;
        try
        {
            await foreach (
                StorageEntry entry in stor.ListAsync(
                    path: outputDirectory,
                    pattern: null,
                    recursive: true,
                    ct: ct
                )
            )
            {
                if (!entry.IsDirectory)
                    total += entry.SizeBytes;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                exception: ex,
                message: "Could not sum output sizes for {Dir} — OutputBytes will be 0",
                args: outputDirectory
            );
        }
        return total;
    }

    private static string ResolvePublishStageName(IStorage dest, bool sameVolume)
    {
        if (dest is LocalStorage)
        {
            return sameVolume
                ? "Publishing artifacts (same-volume rename)"
                : "Publishing artifacts to local";
        }

        string backendLabel = dest.Driver.BackendLabel;

        return $"Publishing artifacts to {backendLabel}";
    }
}
