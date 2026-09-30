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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Encoder;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Metadata;
using NoMercy.Encoder.Naming;
using NoMercy.Encoder.Orchestration;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Reconciliation;
using NoMercy.Encoder.Subtitles;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.MediaProcessing.Jobs.MediaJobs.Support;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Resources;
using NoMercy.Storage;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Resources;
using Serilog.Events;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using MediaType = NoMercy.Encoder.Naming.MediaType;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

/// <summary>
/// The one job type for video encoding — coordinator and executor at once.
/// Implements a durable, self-rescheduling state machine so that server
/// restarts between bundles do not orphan the encode run, and implements
/// <see cref="IHasResourceRequirement"/>/<see cref="IResourceDegradable"/> so
/// the SAME row is what the queue's resource-budget gate reads before every
/// wake-up decides whether this job runs next.
///
/// <para><b>Phase flow for decomposable strategies (HLS, DASH):</b>
/// <c>Initial</c> → decomposes, plans resource-proportional bundles, saves state,
/// re-enqueues self (ungated — analysis only) →
/// <c>WaitChildren</c> → each wake-up runs ONE bundle inline (gated by
/// <see cref="ResourceRequirement"/> for that bundle) and re-enqueues for the
/// next, or transitions once every bundle has succeeded →
/// <c>Finalize</c> → opens fresh <see cref="MediaContext"/>, runs post-encode, completes.</para>
///
/// <para><b>Phase flow for two-pass strategies:</b> the same sequential-bundle
/// mechanism runs the Pass1 bundles first (<c>WaitPass1</c>); once every Pass1
/// bundle has succeeded, the coordinator resolves the shared stats path,
/// patches it onto the pending Pass2 bundles, and switches to running them the
/// same way under <c>WaitChildren</c>.</para>
///
/// <para><b>Whole-task path:</b> coordinator resolves a single <see cref="EncodeTaskKind.Whole"/>
/// task and runs it inline — no coordinator state, no re-enqueue, matches original behavior.</para>
///
/// <para><b>No closure captures:</b> every phase wake-up reads everything it needs from
/// the database and the job payload. No <see cref="MediaContext"/> or object references
/// survive across <see cref="Handle"/> invocations.</para>
/// </summary>
public class VideoEncodeJob
    : AbstractEncoderJob,
        IJobIdReceiver,
        IJobStorageInjector,
        ISelfRescheduling,
        IHasResourceRequirement,
        IResourceDegradable
{
    private IEncodingOrchestrator? _encodingOrchestrator;
    private IHardwareBenchmark? _hardwareBenchmark;
    private IHardwareCapabilities? _hardwareCapabilities;
    private IEncoderProcessRegistry? _encoderProcessRegistry;
    private IMediaAnalyzer? _mediaAnalyzer;
    private IFilenameParserPipeline? _filenameParser;
    private ISubtitleOcrEngine? _subtitleOcrEngine;
    private IEncodeReconciler? _encodeReconciler;

    // Host shutdown signal for the post-encode scan retry backoff — falls back to
    // CancellationToken.None when the DI scope has no IHostApplicationLifetime
    // (e.g. a minimal test scope) so the retry loop still runs, just without an
    // early-exit on server shutdown.
    private CancellationToken _shutdownToken;

    public new void InjectStorageServices(IServiceProvider serviceProvider)
    {
        base.InjectStorageServices(serviceProvider);
        _encodingOrchestrator = serviceProvider.GetRequiredService<IEncodingOrchestrator>();
        _hardwareBenchmark = serviceProvider.GetRequiredService<IHardwareBenchmark>();
        _hardwareCapabilities = serviceProvider.GetRequiredService<IHardwareCapabilities>();
        _encoderProcessRegistry = serviceProvider.GetRequiredService<IEncoderProcessRegistry>();
        _mediaAnalyzer = serviceProvider.GetRequiredService<IMediaAnalyzer>();
        _filenameParser = serviceProvider.GetRequiredService<IFilenameParserPipeline>();
        _subtitleOcrEngine = serviceProvider.GetRequiredService<ISubtitleOcrEngine>();
        _encodeReconciler = serviceProvider.GetRequiredService<IEncodeReconciler>();
        _shutdownToken =
            serviceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping
            ?? CancellationToken.None;
    }

    public override string QueueName => "encoder";
    public override int Priority => 4;
    public string Status { get; set; } = "pending";

    /// <summary>
    /// Durable phase state serialized into the job payload on every re-enqueue.
    /// Null on the initial run (no state yet). Presence of this field drives
    /// the state machine into the correct wake-up branch.
    /// </summary>
    public CoordinatorState? Coordinator { get; set; }

    /// <summary>
    /// When set, encoding is limited to the single preset with this id.
    /// Null preserves the default all-presets behavior.
    /// </summary>
    public Ulid? PresetId { get; set; }

    /// <summary>
    /// Operator escape hatch: when true, reconciliation is skipped entirely
    /// and every preset is fully re-encoded, regardless of what is already
    /// on disk or whether its profile fingerprint matches. Defaults to false
    /// so every existing serialized job (and every dashboard "redispatch")
    /// keeps today's reconciled behavior without needing to set anything.
    /// </summary>
    public bool ForceFullReencode { get; set; }

    private int _selfJobId;

    public void ReceiveJobId(int jobId) => _selfJobId = jobId;

    /// <summary>
    /// Set once this handler has rewritten its own queue row for the next phase.
    /// The worker reads it to decide whether the row is finished work it should
    /// delete, or a live coordinator waiting on its next wake-up.
    /// </summary>
    public bool RescheduledInPlace { get; private set; }

    /// <inheritdoc/>
    /// <summary>
    /// Null while decomposing (that phase is cheap ffprobe/analysis work and
    /// stays ungated, matching the original coordinator's behavior) or once
    /// every bundle has succeeded. Otherwise the requirement of the bundle
    /// this row will run INLINE the moment a worker reserves it next — the
    /// queue worker's budget gate reads this BEFORE calling
    /// <see cref="Handle"/>, so "the dispatcher picks the first FIFO item and
    /// right then decides how to process it" falls out of the normal
    /// reserve-then-gate sequence instead of a second dispatch step.
    /// </summary>
    public ResourceRequirement? ResourceRequirement =>
        Coordinator?.Bundles is { Length: > 0 } bundles
        && Coordinator.CurrentBundleIndex < bundles.Length
            ? bundles[Coordinator.CurrentBundleIndex].Resources
            : null;

    /// <summary>
    /// Re-plans the bundle this row is about to run without its GPU
    /// requirement — see <see cref="IResourceDegradable"/>. Called by the
    /// queue worker's budget gate when that bundle's
    /// <see cref="ResourceRequirement.GpuDeviceKey"/> is not a registered
    /// device on this host. Returns null (nothing to degrade) when there is
    /// no active bundle or it is already CPU-only.
    /// </summary>
    public IShouldQueue? DegradeToSoftware()
    {
        CoordinatorState? state = Coordinator;
        if (
            state?.Bundles is not { Length: > 0 } bundles
            || state.CurrentBundleIndex >= bundles.Length
        )
            return null;

        DecomposedTask current = bundles[state.CurrentBundleIndex];
        if (current.Resources?.GpuDeviceKey is null)
            return null; // already CPU-only — nothing to degrade

        // Same reasoning as the original per-task degrade: without a GPU this
        // becomes a full software encode, which needs a software encode's CPU
        // share, not the smaller hardware-task reservation. Max, never
        // assignment — never shrinks an already-larger reservation.
        DecomposedTask[] degradedBundles = [.. bundles];
        degradedBundles[state.CurrentBundleIndex] = current with
        {
            Resources = current.Resources with
            {
                GpuDeviceKey = null,
                GpuSlots = 0,
                CpuThreads = Math.Max(
                    current.Resources.CpuThreads,
                    EncodeThreadBudget.SoftwareEncode
                ),
            },
        };

        Coordinator = state with { Bundles = degradedBundles };
        return this;
    }

    public override async Task Handle()
    {
        if (Coordinator is not null)
        {
            await HandleCoordinatorWakeUpAsync();
            return;
        }

        await HandleInitialRunAsync();
    }

    // ------------------------------------------------------------------
    // Initial run: decompose + dispatch (or inline for Whole tasks)
    // ------------------------------------------------------------------

    private async Task HandleInitialRunAsync()
    {
        await using MediaContext context = new();
        await using LibraryRepository libraryRepository = new(context, StorageDriver);
        FileRepository fileRepository = new(context, StorageDriver);
        FileManager fileManager = new(
            fileRepository,
            StorageFactory,
            StorageDriver,
            _mediaAnalyzer!,
            _filenameParser!,
            PluginMediaSources
        );

        Folder? folder = await libraryRepository.GetLibraryFolder(FolderId);
        if (folder is null)
            return;

        List<EncodingPreset> presets = folder
            .EncodingPresetFolders.Where(link => link.Preset is not null)
            .Select(link => link.Preset!)
            .ToList();

        if (PresetId is not null)
        {
            presets = presets.Where(preset => preset.Id == PresetId.Value).ToList();
            if (presets.Count == 0)
                Log.LogWarning(
                    "[VideoEncodeJob] PresetId {Value} not found in folder {FolderId} — no presets to run",
                    PresetId.Value,
                    FolderId
                );
        }

        if (presets.Count == 0)
            return;

        FileMetadata fileMetadata = await GetFileMetaData(folder, context);
        if (!fileMetadata.Success)
            return;

        Stopwatch stopwatch = Stopwatch.StartNew();

        IStorage destinationStorage = StorageFactory.For(folder.Id, folder.DriverId, folder.Path);

        IStorage sourceStorage = SourceStorageResolver.Resolve(
            StorageFactory,
            SourceDriverId,
            InputFile,
            folder,
            destinationStorage
        );

        // Resolve every selected preset and reconcile it against what's
        // already on disk BEFORE dispatching anything. This is what lets a
        // folder with several presets (e.g. "4K HDR HEVC" + "1080p SDR HEVC")
        // become ONE coordinated encode below instead of one independent
        // VideoEncodeJob-style run per preset — the bug this state machine
        // used to have when the per-preset foreach dispatched immediately.
        List<PlannedPreset> planned = [];

        try
        {
            foreach (EncodingPreset preset in presets)
            {
                EncodingProfile encodingProfile;
                try
                {
                    encodingProfile = PresetResolver.Resolve(
                        preset.Id,
                        new DbPresetLookup(context)
                    );
                }
                catch (Exception ex)
                {
                    Log.LogWarning(
                        "Skipping preset '{Name}' ({Id}): resolve failed — {Message}",
                        preset.Name,
                        preset.Id,
                        ex.Message
                    );
                    continue;
                }

                if (encodingProfile.Video is null && encodingProfile.Audio.Length == 0)
                {
                    Log.LogWarning(
                        "Skipping preset {Name}: no video or audio outputs configured",
                        preset.Name
                    );
                    continue;
                }

                ReconciliationDecision reconciliation = await ReconcileAsync(
                    encodingProfile,
                    fileMetadata,
                    sourceStorage,
                    destinationStorage
                );

                if (reconciliation.Action == ReconciliationAction.Skip)
                {
                    Log.LogInformation(
                        "[VideoEncodeJob] Reconciliation: skipping preset '{Name}' for {Id} — {Reason}",
                        preset.Name,
                        fileMetadata.Id,
                        reconciliation.Reason
                    );
                    continue;
                }

                planned.Add(new(preset, encodingProfile, reconciliation));
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Video encode task failed");

            await EncoderCardTerminator.PublishFailedAsync(
                fileMetadata.Id,
                fileMetadata.Title,
                InputFile,
                ex.Message,
                ex.GetType().Name,
                fileMetadata.PosterPath,
                fileMetadata.ImgPath
            );

            throw;
        }

        if (planned.Count == 0)
            return;

        // OCR-only top-ups (every real output already valid, only the
        // bitmap-subtitle sidecar missing) run ONCE for the whole batch —
        // the sidecar is source-derived, identical no matter which preset
        // asked for it. Running it once per preset was the double-OCR half
        // of the bug this coordinated-encode work fixes.
        List<PlannedPreset> ocrOnly = planned
            .Where(entry =>
                entry.Decision is { Action: ReconciliationAction.Partial, MissingKinds.Count: 0 }
            )
            .ToList();

        if (ocrOnly.Count > 0)
        {
            foreach (PlannedPreset entry in ocrOnly)
                Log.LogInformation(
                    "[VideoEncodeJob] Reconciliation: preset '{Name}' for {Id} is fully encoded — running OCR top-up only ({Reason})",
                    entry.Preset.Name,
                    fileMetadata.Id,
                    entry.Decision.Reason
                );

            await RunOcrTopUpAsync(
                fileMetadata,
                sourceStorage,
                destinationStorage,
                fileManager,
                folder
            );
        }

        List<PlannedPreset> needsWork = planned.Except(ocrOnly).ToList();
        if (needsWork.Count == 0)
            return;

        // A merged run needs one consistent reconciliation verdict across
        // every preset it covers: a Partial top-up must not rewrite the
        // master (it only fills gaps), so mixing it with a Full re-encode
        // — which must rewrite the master to list every rendition — has no
        // single correct answer. A single preset trivially qualifies (its
        // own verdict is the only one that matters); several presets only
        // qualify when they all agree the whole thing needs a fresh encode.
        bool canMerge =
            needsWork.Count == 1
            || needsWork.All(entry => entry.Decision.Action == ReconciliationAction.Full);

        try
        {
            if (canMerge)
            {
                try
                {
                    await RunMergedEncodeAsync(
                        needsWork,
                        fileMetadata,
                        stopwatch,
                        sourceStorage,
                        destinationStorage,
                        context,
                        fileManager,
                        folder
                    );
                    return;
                }
                catch (MergedEncodingIncompatibleException ex)
                {
                    Log.LogWarning(
                        "[VideoEncodeJob] Merged decompose unavailable for folder {FolderId} ({Reason}) — falling back to independent per-preset dispatch for {Count} preset(s).",
                        FolderId,
                        ex.Message,
                        needsWork.Count
                    );
                }
            }

            foreach (PlannedPreset entry in needsWork)
            {
                await RunSinglePresetEncodeAsync(
                    entry.Preset,
                    entry.Profile,
                    entry.Decision,
                    fileMetadata,
                    stopwatch,
                    sourceStorage,
                    destinationStorage,
                    context,
                    fileManager,
                    folder
                );
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Video encode task failed");

            await EncoderCardTerminator.PublishFailedAsync(
                fileMetadata.Id,
                fileMetadata.Title,
                InputFile,
                ex.Message,
                ex.GetType().Name,
                fileMetadata.PosterPath,
                fileMetadata.ImgPath
            );

            throw;
        }
    }

    /// <summary>
    /// One preset resolved to a profile and reconciled against what's already
    /// on disk, still waiting to be dispatched. Bridges the resolve/reconcile
    /// pass in <see cref="HandleInitialRunAsync"/> to the merged and
    /// per-preset dispatch paths below it.
    /// </summary>
    private sealed record PlannedPreset(
        EncodingPreset Preset,
        EncodingProfile Profile,
        ReconciliationDecision Decision
    );

    /// <summary>
    /// The smart-orchestrator path: builds one <see cref="EncodingRequest"/>
    /// per preset in <paramref name="needsWork"/> and asks the orchestrator to
    /// decompose them as ONE coordinated encode — a single output folder, a
    /// single master playlist listing every preset's video rendition, with
    /// audio / subtitles / thumbnails / chapters produced once and shared.
    /// Throws <see cref="MergedEncodingIncompatibleException"/> (propagated
    /// from <see cref="IEncodingOrchestrator.DecomposeMergedAsync"/>, or
    /// raised here when a merged decompose resolves to a single Whole task
    /// for more than one preset — a single-file container can only ever hold
    /// one preset's output) so the caller can fall back to dispatching each
    /// preset independently.
    /// </summary>
    private async Task RunMergedEncodeAsync(
        List<PlannedPreset> needsWork,
        FileMetadata fileMetadata,
        Stopwatch stopwatch,
        IStorage sourceStorage,
        IStorage destinationStorage,
        MediaContext context,
        FileManager fileManager,
        Folder folder
    )
    {
        if (EventBusProvider.IsConfigured)
        {
            await EventBusProvider.Current.PublishAsync(
                new EncodingStartedEvent
                {
                    JobId = fileMetadata.Id,
                    InputPath = InputFile,
                    OutputPath = fileMetadata.Path,
                    ProfileName = string.Join(" + ", needsWork.Select(entry => entry.Preset.Name)),
                }
            );
        }

        IEncodingOrchestrator orchestrator = _encodingOrchestrator!;

        List<EncodingRequest> requests = needsWork
            .Select(entry => new EncodingRequest(
                InputPath: InputFile,
                OutputDirectory: fileMetadata.Path,
                Profile: entry.Profile,
                MediaTitle: fileMetadata.FileName,
                SourceStorage: sourceStorage,
                DestinationStorage: destinationStorage,
                // Same identity every request in the run carries — see the
                // matching comment in RunSinglePresetEncodeAsync.
                MediaItem: fileMetadata.MediaItem
            ))
            .ToList();

        string groupTag = Ulid.NewUlid().ToString();
        (OutputPlan? plan, DecomposedTask[] tasks) =
            await orchestrator.DecomposeMergedWithPlanAsync(requests, groupTag);

        // A partial top-up only ever applies when the merge covers exactly
        // one preset — canMerge in HandleInitialRunAsync requires every
        // OTHER member of a multi-preset merge to be a clean Full re-encode,
        // since a top-up must not rewrite the master and a Full run must.
        bool isPartialTopUp = false;
        if (
            needsWork is [{ Decision.Action: ReconciliationAction.Partial }]
            && needsWork[0].Decision.MissingKinds.Count > 0
        )
        {
            ReconciliationDecision decision = needsWork[0].Decision;
            tasks = tasks.Where(task => decision.MissingKinds.Contains(task.Kind)).ToArray();
            isPartialTopUp = true;

            if (tasks.Length == 0)
            {
                Log.LogWarning(
                    "[VideoEncodeJob] Reconciliation flagged {Kinds} as missing for preset '{Name}' but decomposition produced no matching task — falling back to a full re-encode",
                    string.Join(", ", decision.MissingKinds),
                    needsWork[0].Preset.Name
                );
                (plan, tasks) = await orchestrator.DecomposeMergedWithPlanAsync(requests, groupTag);
                isPartialTopUp = false;
            }
        }

        bool isWhole = tasks is [{ Kind: EncodeTaskKind.Whole }];

        if (isWhole)
        {
            if (needsWork.Count > 1)
                throw new MergedEncodingIncompatibleException(
                    "Merged decompose produced a single Whole (single-file container) task "
                        + $"for {needsWork.Count} presets — a single-file output can only ever "
                        + "hold one preset's encode."
                );

            await RunInlineAsync(
                orchestrator,
                requests[0],
                needsWork[0].Profile,
                needsWork[0].Preset,
                fileMetadata,
                stopwatch,
                sourceStorage,
                context,
                fileManager,
                folder
            );
            return;
        }

        // Non-Whole tasks only ever come back from a plan that decomposed
        // successfully — DecomposeMergedWithPlanAsync's null Plan cases both
        // collapse to the single Whole fallback handled above, so this can
        // never actually fire; it documents the invariant instead of casting
        // it away with a null-forgiving operator.
        OutputPlan resolvedPlan =
            plan
            ?? throw new InvalidOperationException(
                "DecomposeMergedWithPlanAsync returned decomposed tasks without a Plan — "
                    + "the decode-aware bundler cannot classify outputs without it."
            );

        Ulid[] presetIds = needsWork.Select(entry => entry.Preset.Id).ToArray();
        await DispatchDecomposedAsync(
            tasks,
            resolvedPlan,
            presetIds,
            fileMetadata,
            stopwatch,
            isPartialTopUp
        );
    }

    /// <summary>
    /// The legacy path, preserved verbatim for callers that can't merge: a
    /// single selected preset (the common case — most folders carry exactly
    /// one), and the rare fallback when <see cref="RunMergedEncodeAsync"/>
    /// throws <see cref="MergedEncodingIncompatibleException"/> or when
    /// several presets disagree on what reconciliation needs to do. Each
    /// preset gets its own independent encode, exactly as every
    /// <see cref="VideoEncodeJob"/> run did before the smart orchestrator.
    /// </summary>
    private async Task RunSinglePresetEncodeAsync(
        EncodingPreset preset,
        EncodingProfile encodingProfile,
        ReconciliationDecision reconciliation,
        FileMetadata fileMetadata,
        Stopwatch stopwatch,
        IStorage sourceStorage,
        IStorage destinationStorage,
        MediaContext context,
        FileManager fileManager,
        Folder folder
    )
    {
        if (EventBusProvider.IsConfigured)
        {
            await EventBusProvider.Current.PublishAsync(
                new EncodingStartedEvent
                {
                    JobId = fileMetadata.Id,
                    InputPath = InputFile,
                    OutputPath = fileMetadata.Path,
                    ProfileName = preset.Name,
                }
            );
        }

        IEncodingOrchestrator orchestrator = _encodingOrchestrator!;

        EncodingRequest request = new(
            InputPath: InputFile,
            OutputDirectory: fileMetadata.Path,
            Profile: encodingProfile,
            MediaTitle: fileMetadata.FileName,
            SourceStorage: sourceStorage,
            DestinationStorage: destinationStorage,
            // Pure identity — safe on every request, including the Whole-task
            // inline path below (RunInlineAsync runs Build+Execute+Finalize
            // over this exact request). Drives BundleLayout resolution in
            // PlanStage so FinalizeStage writes manifest.json/reconstruction.json
            // for every encode, not only the coordinator's FinalizeOnly pass.
            // EncodingOptions.EnableMetadataInjection stays unset (defaults
            // false) here, so the emitted ffmpeg command is unaffected. Null
            // when the source has no resolvable movie/episode (e.g. a disc
            // rip) — degrades to today's behavior exactly.
            MediaItem: fileMetadata.MediaItem
        );

        string groupTag = Ulid.NewUlid().ToString();
        (OutputPlan? plan, DecomposedTask[] tasks) = await orchestrator.DecomposeWithPlanAsync(
            request,
            groupTag
        );

        // A partial top-up rebuilds only the missing renditions; a bundle
        // built from it must not rewrite the master, which already lists
        // the whole set. Cleared if the filter empties and we fall back to
        // a full re-encode below.
        bool isPartialTopUp = false;

        // Partial with a non-empty MissingKinds only ever happens for
        // decomposable (HLS/DASH) strategies — DecideSingleFile never
        // returns Partial with missing kinds, only Skip/Full — so
        // filtering down to the missing kinds here never starves a
        // Whole-task (MKV/MP4) run of its only task.
        if (reconciliation is { Action: ReconciliationAction.Partial, MissingKinds.Count: > 0 })
        {
            tasks = tasks.Where(task => reconciliation.MissingKinds.Contains(task.Kind)).ToArray();
            isPartialTopUp = true;

            if (tasks.Length == 0)
            {
                Log.LogWarning(
                    "[VideoEncodeJob] Reconciliation flagged {Kinds} as missing for preset '{Name}' but decomposition produced no matching task — falling back to a full re-encode",
                    string.Join(", ", reconciliation.MissingKinds),
                    preset.Name
                );
                (plan, tasks) = await orchestrator.DecomposeWithPlanAsync(request, groupTag);
                isPartialTopUp = false;
            }
        }

        bool isWhole = tasks is [{ Kind: EncodeTaskKind.Whole }];

        if (isWhole)
        {
            await RunInlineAsync(
                orchestrator,
                request,
                encodingProfile,
                preset,
                fileMetadata,
                stopwatch,
                sourceStorage,
                context,
                fileManager,
                folder
            );
            return;
        }

        // See the matching guard in RunMergedEncodeAsync: non-Whole tasks
        // only ever come back from a plan that decomposed successfully.
        OutputPlan resolvedPlan =
            plan
            ?? throw new InvalidOperationException(
                "DecomposeWithPlanAsync returned decomposed tasks without a Plan — "
                    + "the decode-aware bundler cannot classify outputs without it."
            );

        await DispatchDecomposedAsync(
            tasks,
            resolvedPlan,
            [preset.Id],
            fileMetadata,
            stopwatch,
            isPartialTopUp
        );
    }

    // ------------------------------------------------------------------
    // Coordinator state machine (subsequent wake-ups)
    // ------------------------------------------------------------------

    private async Task HandleCoordinatorWakeUpAsync()
    {
        CoordinatorState state = Coordinator!;

        // No wake-up trace — the coordinator re-enters this method on every
        // re-enqueue tick (multiple times per second while bundles encode),
        // and even at Verbose it bombards the console. The state-transition
        // logs further down already announce real progress.

        try
        {
            switch (state.Phase)
            {
                case CoordinatorPhase.WaitPass1:
                    await HandleWaitPass1Async(state);
                    break;

                case CoordinatorPhase.WaitChildren:
                    await HandleWaitChildrenAsync(state);
                    break;

                case CoordinatorPhase.Finalize:
                    await HandleFinalizeAsync(state);
                    break;

                default:
                    Log.LogWarning(
                        "[VideoEncodeJob] Unknown coordinator phase '{Phase}' — completing job",
                        state.Phase
                    );
                    break;
            }
        }
        catch (Exception ex)
        {
            // A phase that throws after children have reported progress strands
            // the dashboard card on its last message. Clear it before the
            // exception propagates to the queue's failure handling.
            await EncoderCardTerminator.PublishFailedAsync(
                Id.ToInt(),
                string.Empty,
                InputFile,
                ex.Message,
                ex.GetType().Name
            );
            throw;
        }
    }

    private async Task HandleWaitPass1Async(CoordinatorState state)
    {
        await using MediaContext context = new();

        DecomposedTask[] pass1Bundles = state.Bundles ?? [];

        if (pass1Bundles.Length > 0 && state.CurrentBundleIndex < pass1Bundles.Length)
        {
            bool allPass1Done = await RunSequentialBundlesAsync(
                state,
                pass1Bundles,
                context,
                CoordinatorPhase.WaitPass1
            );

            if (!allPass1Done)
                return; // re-enqueued to run the next Pass1 bundle
        }

        // Every Pass1 bundle has succeeded — resolve the shared stats file
        // path and patch it onto the pending Pass2 bundles.
        EncodeTaskOutcome? anyPass1Outcome = await context
            .EncodeTaskOutcomes.AsNoTracking()
            .FirstOrDefaultAsync(o =>
                o.GroupTag == state.GroupTag && o.Kind == nameof(EncodeTaskKind.Pass1)
            );

        string pass1StatsPath =
            anyPass1Outcome?.OutputArtifactsJson?.Split('\n').FirstOrDefault() ?? string.Empty;

        DecomposedTask[] pass2AndOtherBundles =
        [
            .. (state.PendingBundles ?? []).Select(task =>
                task.Kind == EncodeTaskKind.Pass2
                    ? task with
                    {
                        StatsFilePath = pass1StatsPath,
                    }
                    : task
            ),
        ];

        Log.LogInformation(
            "[VideoEncodeJob] All Pass1 bundles complete — {Length} Pass2/other bundle(s) queued. Transitioning to WaitChildren.",
            pass2AndOtherBundles.Length
        );

        // Always through WaitChildren, even on the (never-expected in
        // practice — every Pass1 bundle pairs with a Pass2 bundle) empty
        // case: HandleWaitChildrenAsync already treats an empty/exhausted
        // Bundles array as "nothing left, go to Finalize" on its own next
        // wake-up, so WaitPass1 never needs its own shortcut around it.
        ReEnqueueSelf(
            state with
            {
                Phase = CoordinatorPhase.WaitChildren,
                Pass1StatsPath = pass1StatsPath,
                Pass2DispatchedAt = DateTime.UtcNow,
                Bundles = pass2AndOtherBundles,
                PendingBundles = null,
                CurrentBundleIndex = 0,
            },
            TimeSpan.Zero
        );
    }

    private async Task HandleWaitChildrenAsync(CoordinatorState state)
    {
        await using MediaContext context = new();

        DecomposedTask[] bundles = state.Bundles ?? [];

        if (bundles.Length == 0 || state.CurrentBundleIndex >= bundles.Length)
        {
            Log.LogTrace(
                "[VideoEncodeJob] WaitChildren: nothing left to run. Transitioning to Finalize."
            );
            ReEnqueueSelf(state with { Phase = CoordinatorPhase.Finalize }, TimeSpan.Zero);
            return;
        }

        bool allBundlesDone = await RunSequentialBundlesAsync(
            state,
            bundles,
            context,
            CoordinatorPhase.WaitChildren
        );

        if (!allBundlesDone)
            return; // re-enqueued to run the next bundle

        Log.LogInformation(
            "[VideoEncodeJob] All {Length} bundle(s) complete. Transitioning to Finalize.",
            bundles.Length
        );
        // Finalize is one-shot post-encode work — fire immediately.
        ReEnqueueSelf(state with { Phase = CoordinatorPhase.Finalize }, TimeSpan.Zero);
    }

    /// <summary>
    /// The dispatcher, collapsed: ensures the bundle at
    /// <paramref name="bundles"/>[state.CurrentBundleIndex] has succeeded,
    /// running its ffmpeg command INLINE right now when it has not been
    /// attempted yet — this <see cref="Handle"/> call already passed the
    /// resource-budget gate for exactly this bundle's <see cref="ResourceRequirement"/>,
    /// so there is nothing left to arbitrate before running it. A bundle
    /// that fails throws, so the queue's own attempt/retry/dead-letter
    /// accounting governs it exactly like any other job — no separate child
    /// row, no separate retry budget.
    /// <para>
    /// Re-enqueues the coordinator (available immediately — there is no
    /// external completion left to poll for) and returns false when there is
    /// more work in <paramref name="bundles"/>; returns true once every
    /// bundle has succeeded, so the caller decides the next phase.
    /// </para>
    /// </summary>
    private async Task<bool> RunSequentialBundlesAsync(
        CoordinatorState state,
        DecomposedTask[] bundles,
        MediaContext context,
        CoordinatorPhase phaseWhileRunning
    )
    {
        DecomposedTask current = bundles[state.CurrentBundleIndex];
        string[] currentTaskIds = current.BundledTaskIds ?? [current.TaskId];

        List<EncodeTaskOutcome> currentOutcomes = await context
            .EncodeTaskOutcomes.AsNoTracking()
            .Where(o => o.GroupTag == state.GroupTag && currentTaskIds.Contains(o.TaskId))
            .ToListAsync();

        bool alreadySucceeded =
            currentOutcomes.Count > 0 && currentOutcomes.All(outcome => outcome.Success);

        if (!alreadySucceeded)
        {
            (bool success, string? error) = await RunBundleInlineAsync(
                current,
                state.PresetId,
                state.PresetIds,
                state.OutputDirectory
            );

            if (!success)
            {
                throw new InvalidOperationException(
                    $"Encode bundle '{current.TaskId}' failed: {error ?? "no error recorded"}"
                );
            }
        }

        int nextIndex = state.CurrentBundleIndex + 1;
        if (nextIndex < bundles.Length)
        {
            Log.LogInformation(
                "[VideoEncodeJob] Bundle {CurrentBundleIndex}/{Length} complete. Running bundle {NextIndex}/{Length2} on next wake-up.",
                state.CurrentBundleIndex + 1,
                bundles.Length,
                nextIndex + 1,
                bundles.Length
            );
            ReEnqueueSelf(
                state with
                {
                    Phase = phaseWhileRunning,
                    CurrentBundleIndex = nextIndex,
                },
                TimeSpan.Zero
            );
            return false;
        }

        return true;
    }

    private async Task HandleFinalizeAsync(CoordinatorState state)
    {
        // Open a fresh MediaContext for finalize — nothing is captured from
        // any prior Handle() invocation. This is the key B1 fix.
        await using MediaContext context = new();

        FileRepository fileRepository = new(context, StorageDriver);
        FileManager fileManager = new(
            fileRepository,
            StorageFactory,
            StorageDriver,
            _mediaAnalyzer!,
            _filenameParser!,
            PluginMediaSources
        );

        await using LibraryRepository libraryRepository = new(context, StorageDriver);
        Folder? folder = await libraryRepository.GetLibraryFolder(FolderId);
        if (folder is null)
        {
            Log.LogWarning(
                "[VideoEncodeJob] Finalize: folder {FolderId} not found — aborting post-encode",
                FolderId
            );
            return;
        }

        FileMetadata fileMetadata = await GetFileMetaData(folder, context);
        if (!fileMetadata.Success)
            return;

        List<EncodeTaskOutcome> outcomes = await context
            .EncodeTaskOutcomes.AsNoTracking()
            .Where(o => o.GroupTag == state.GroupTag)
            .ToListAsync();

        int failedCount = outcomes.Count(o => !o.Success);

        // Computed once, ahead of the failure branch too: a failed run is
        // terminal (this method returns rather than throws, so the queue
        // never retries it), which makes it safe to delete the shared
        // tempDir here instead of leaving it for the boot-only orphan
        // sweeper to eventually reclaim.
        string relativeOutputPath = (fileMetadata.Path ?? string.Empty)
            .Replace('\\', '/')
            .Trim('/');
        string tempDir = Path.Combine(
            StoragePaths.TranscodeRoot,
            relativeOutputPath.Replace('/', Path.DirectorySeparatorChar)
        );

        if (failedCount > 0)
        {
            Log.LogWarning(
                "[VideoEncodeJob] Finalize: {FailedCount} task(s) failed — skipping post-encode",
                failedCount
            );

            await EncoderCardTerminator.PublishFailedAsync(
                fileMetadata.Id,
                fileMetadata.Title,
                InputFile,
                $"{failedCount} rung(s) failed",
                "FinalizeChildFailed",
                fileMetadata.PosterPath,
                fileMetadata.ImgPath
            );

            (IReadOnlyList<string> failedDescriptors, string? lastError) = SummarizeFailures(
                outcomes.Where(o => !o.Success).ToList()
            );

            await new IncompleteEncodeRecorder().RecordAsync(
                context,
                mediaId: fileMetadata.Id,
                folderId: FolderId.ToString(),
                title: fileMetadata.Title,
                missingKeys: failedDescriptors,
                lastError: lastError,
                attemptsMade: 0,
                ct: CancellationToken.None
            );

            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (Exception ex)
                {
                    Log.LogWarning(
                        ex,
                        "[VideoEncodeJob] Finalize: failed to delete tempDir '{TempDir}' after a failed encode — leaving it for the orphan sweeper",
                        tempDir
                    );
                }
            }

            return;
        }

        IStorage destinationStorage = StorageFactory.For(folder.Id, folder.DriverId, folder.Path);

        IStorage sourceStorage = SourceStorageResolver.Resolve(
            StorageFactory,
            SourceDriverId,
            InputFile,
            folder,
            destinationStorage
        );

        // Pre-flight check: ensure the shared tempDir exists and contains at least
        // some expected output before proceeding to FinalizeOnly.

        // A dispatch-time bundle is a Whole task, and a Whole task is "the only
        // execution": it runs FinalizeStage itself and publishes the tempDir to the
        // destination, which is precisely why the tempDir is empty by the time we
        // get here. Only per-stream slices defer their finalize to this pass, and a
        // run made entirely of bundles has nothing left for it to do.
        //
        // The emptiness check below is meant to catch children that produced
        // nothing. Applying it to a run whose bundles already published turned a
        // success into a failure and skipped everything after it — the subtitle
        // OCR, the library refresh, the completion event — for every bundled
        // encode.
        bool bundlesSelfFinalized =
            state.Bundles is { Length: > 0 } bundles
            && bundles.All(bundle => bundle.Kind == EncodeTaskKind.Whole);

        if (
            !bundlesSelfFinalized
            && (
                !Directory.Exists(tempDir)
                || !Directory.EnumerateFiles(tempDir, "*.m3u8", SearchOption.AllDirectories).Any()
            )
        )
        {
            Log.LogError(
                "[VideoEncodeJob] Finalize: tempDir '{TempDir}' missing or empty. Cannot finalize GroupTag={GroupTag}.",
                tempDir,
                state.GroupTag
            );
            return;
        }

        // Coordinator finalize: run the pipeline once over the shared cache
        // tempDir with Options.FinalizeOnly=true. The orchestrator skips
        // Build + Execute, runs FinalizeStage against the variant playlists
        // the children produced (writing master m3u8, chapters.vtt,
        // fonts.json, manifest.json), then publishes the whole cache to
        // the destination and deletes it. Per-task EncodeAsync runs skip
        // FinalizeStage + publish + cleanup precisely to avoid racing this
        // single coordinator-driven pass.
        IEncodingOrchestrator orchestrator = _encodingOrchestrator!;

        EncodingRequest finalizeRequest;
        await using (MediaContext profileLookup = new())
        {
            if (state.PresetIds is { Length: > 1 } presetIds)
            {
                // Merged run: re-resolve every preset's profile and re-plan +
                // re-merge them (PlanAsync is deterministic given the same
                // profile + source) so FinalizeStage writes a master playlist
                // listing every preset's video rendition instead of
                // re-deriving a plan from just one preset's profile.
                List<EncodingRequest> mergeRequests = new(presetIds.Length);
                bool resolveFailed = false;

                foreach (Ulid presetId in presetIds)
                {
                    try
                    {
                        EncodingProfile presetProfile = PresetResolver.Resolve(
                            presetId,
                            new DbPresetLookup(profileLookup)
                        );
                        mergeRequests.Add(
                            new(
                                InputPath: InputFile,
                                OutputDirectory: fileMetadata.Path ?? string.Empty,
                                Profile: presetProfile,
                                MediaTitle: fileMetadata.FileName,
                                SourceStorage: sourceStorage,
                                DestinationStorage: destinationStorage,
                                MediaItem: fileMetadata.MediaItem
                            )
                        );
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning(
                            "[VideoEncodeJob] Finalize: cannot resolve preset {PresetId} — {Message}",
                            presetId,
                            ex.Message
                        );
                        resolveFailed = true;
                        break;
                    }
                }

                if (resolveFailed)
                    return;

                OutputPlan? mergedPlan = await orchestrator.PlanMergedAsync(mergeRequests);
                if (mergedPlan is null)
                {
                    Log.LogError(
                        "[VideoEncodeJob] Finalize: could not rebuild the merged plan for preset set [{PresetIds}] — aborting post-encode",
                        string.Join(", ", presetIds)
                    );
                    return;
                }

                finalizeRequest = mergeRequests[0] with
                {
                    Options = new(FinalizeOnly: true, PrecomputedPlan: mergedPlan),
                };
            }
            else
            {
                EncodingProfile finalizeProfile;
                try
                {
                    finalizeProfile = PresetResolver.Resolve(
                        state.PresetId,
                        new DbPresetLookup(profileLookup)
                    );
                }
                catch (Exception ex)
                {
                    Log.LogWarning(
                        "[VideoEncodeJob] Finalize: cannot resolve preset {PresetId} — {Message}",
                        state.PresetId,
                        ex.Message
                    );
                    return;
                }

                finalizeRequest = new(
                    InputPath: InputFile,
                    OutputDirectory: fileMetadata.Path ?? string.Empty,
                    Profile: finalizeProfile,
                    Options: new(FinalizeOnly: true),
                    MediaTitle: fileMetadata.FileName,
                    SourceStorage: sourceStorage,
                    DestinationStorage: destinationStorage,
                    // Pure identity — safe here regardless of FinalizeOnly. Drives
                    // BundleLayout resolution + manifest.json/reconstruction.json in
                    // FinalizeStage. EncodingOptions.EnableMetadataInjection stays unset
                    // (defaults false), so this has no effect on the ffmpeg command even
                    // on a request where Build/Execute do run. Null when the source has
                    // no resolvable movie/episode (e.g. a disc rip) — degrades to today's
                    // behavior exactly.
                    MediaItem: fileMetadata.MediaItem
                );
            }
        }

        // The bundles already finalized and published themselves. Running the
        // pipeline again over the tempDir they emptied would only rediscover that
        // there is nothing there. Fall through to post-encode, which is the part
        // that still has work to do. The post-encode scan (FileManager.MakeMetadata)
        // is what rebuilds the master playlist from disk now — see
        // RebuildHlsMasterFromDiskAsync — because it reliably reads the fully-
        // published media root, unlike this coordinator's own scope which can
        // resolve against only the last bundle's own rendition.
        if (bundlesSelfFinalized)
        {
            Log.LogInformation(
                "[VideoEncodeJob] Finalize: bundles for GroupTag={GroupTag} finalized and published themselves; continuing to post-encode.",
                state.GroupTag
            );
        }
        else
        {
            await PublishStageAsync(fileMetadata, "Publishing artifacts");
            try
            {
                EncodingResult publishResult = await orchestrator.EncodeAsync(
                    finalizeRequest,
                    ct: _shutdownToken
                );
                if (!publishResult.Success)
                {
                    string err =
                        publishResult.Error?.Message
                        ?? publishResult.EnrichedError?.Message
                        ?? "finalize-only pass failed with no details";
                    Log.LogError(
                        "[VideoEncodeJob] Coordinator finalize failed for GroupTag={GroupTag}: {Err}",
                        state.GroupTag,
                        err
                    );

                    await new IncompleteEncodeRecorder().RecordAsync(
                        context,
                        mediaId: fileMetadata.Id,
                        folderId: FolderId.ToString(),
                        title: fileMetadata.Title,
                        missingKeys: ["finalize"],
                        lastError: err,
                        attemptsMade: 0,
                        ct: CancellationToken.None
                    );

                    await EncoderCardTerminator.PublishFailedAsync(
                        fileMetadata.Id,
                        fileMetadata.Title,
                        InputFile,
                        err,
                        "FinalizeFailed",
                        fileMetadata.PosterPath,
                        fileMetadata.ImgPath
                    );

                    return;
                }
            }
            catch (Exception ex)
            {
                Log.LogError(
                    "[VideoEncodeJob] Coordinator finalize threw for GroupTag={GroupTag}: {Message}",
                    state.GroupTag,
                    ex.Message
                );
                throw;
            }
        }

        await PublishStageAsync(fileMetadata, "Checking source subtitles");
        await RunBitmapSubtitleOcrAsync(fileMetadata, InputFile, sourceStorage, destinationStorage);

        await PublishStageAsync(fileMetadata, "Refreshing library");
        Library library = folder.FolderLibraries.First().Library;
        await ScanEncodedOutputWithRetryAsync(
            fileManager,
            fileMetadata.Id,
            fileMetadata.Title,
            library,
            DeriveScanFilter(fileMetadata.Path, fileMetadata.FileName)
        );

        await new IncompleteEncodeRecorder().ClearAsync(
            context,
            fileMetadata.Id,
            FolderId.ToString(),
            CancellationToken.None
        );

        if (EventBusProvider.IsConfigured)
        {
            await EventBusProvider.Current.PublishAsync(
                new EncodingCompletedEvent
                {
                    JobId = fileMetadata.Id,
                    OutputPath = fileMetadata.Path ?? string.Empty,
                    Duration = TimeSpan.Zero,
                    PosterPath = fileMetadata.PosterPath,
                    BackdropPath = fileMetadata.ImgPath,
                }
            );
        }

        Log.LogInformation(
            "[VideoEncodeJob] Finalize complete for GroupTag={GroupTag}",
            state.GroupTag
        );
    }

    // ------------------------------------------------------------------
    // Post-encode registration
    // ------------------------------------------------------------------

    private static readonly TimeSpan[] PostEncodeScanRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(25),
    ];

    /// <summary>
    /// Registers the just-published encode output, retrying the filtered scan on a
    /// bounded backoff when it resolves 0 files. Remote-storage directory listings
    /// (NFS <c>acdirmax</c>, S3 read-after-write) can hide a just-created entry for
    /// tens of seconds right after publish, so an immediate scan can come back empty
    /// even though the output was written successfully — the delayed manual rescan a
    /// user runs afterward then "just works" because enough time has passed. The
    /// retried scan is the same filtered, additive call — <see cref="FileManager.FindFiles"/>
    /// never deletes existing records while a <see cref="FileManager.FilterFiles"/> filter
    /// is set — so re-running it here is safe.
    /// </summary>
    private async Task ScanEncodedOutputWithRetryAsync(
        FileManager fileManager,
        int mediaId,
        string title,
        Library library,
        string filterFileName
    )
    {
        for (int attempt = 0; attempt < PostEncodeScanRetryDelays.Length; attempt++)
        {
            TimeSpan delay = PostEncodeScanRetryDelays[attempt];
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, _shutdownToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            fileManager.FilterFiles(filterFileName);
            // mediaId is the id this job was dispatched for (fileMetadata.Id — an
            // episode id for TV/anime). Carry it through so registration attaches
            // the output to the episode we were actually encoding, not whatever
            // episode the output filename happens to parse to. See
            // FileManager.DispatchedMediaId for the failure this prevents.
            fileManager.HintDispatchedMediaId(mediaId);
            bool hasCandidates = await fileManager.FindFiles(mediaId, library);
            if (hasCandidates)
                return;
        }

        Log.LogWarning(
            "[VideoEncodeJob] Post-encode registration found 0 files for id={Id} '{Title}' (filter='{Filter}') after {Attempts} attempts — a manual rescan will be required; check storage visibility/naming",
            mediaId,
            title,
            filterFileName,
            PostEncodeScanRetryDelays.Length
        );
    }

    // ------------------------------------------------------------------
    // Quarantine helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the quarantine descriptor list and first error from a set of
    /// failed <see cref="EncodeTaskOutcome"/> rows. Pure — no I/O.
    /// </summary>
    /// <summary>
    /// Chooses the filter the post-encode registration scan runs with. Anchors on
    /// the leaf of the output directory the encoder just wrote to
    /// (<paramref name="outputPath"/>) rather than the reconstructed file name.
    /// <paramref name="fallbackFileName"/> is <c>CreateFileName()</c>
    /// (<c>show.SxxExx.episodeTitle.NoMercy</c>); its episode-title segment is
    /// re-cleaned at scan time and drifts from the name written at encode time
    /// (apostrophes, unicode, a changed cleaning rule), so a
    /// <c>file.Contains(filter)</c> match against it silently registered nothing
    /// and left users forcing an unfiltered rescan. The directory leaf carries the
    /// stable <c>show.SxxExx</c> (or <c>movie.(year)</c>) token every output file
    /// lives under, and is exactly the folder this run wrote into. Falls back to
    /// the file name only when the output path is empty.
    /// </summary>
    internal static string DeriveScanFilter(string? outputPath, string fallbackFileName)
    {
        string leaf =
            outputPath
                ?.Trim('/', '\\')
                .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()
            ?? string.Empty;

        return string.IsNullOrWhiteSpace(leaf) ? fallbackFileName : leaf;
    }

    internal static (IReadOnlyList<string> descriptors, string? lastError) SummarizeFailures(
        IReadOnlyList<EncodeTaskOutcome> failedOutcomes
    )
    {
        Dictionary<string, int> kindCounts = [];

        foreach (EncodeTaskOutcome outcome in failedOutcomes)
        {
            kindCounts.TryGetValue(outcome.Kind, out int count);
            kindCounts[outcome.Kind] = count + 1;
        }

        List<string> descriptors = kindCounts
            .Select(pair => pair.Value > 1 ? $"{pair.Key} ({pair.Value}x)" : pair.Key)
            .ToList();

        string? lastError = failedOutcomes
            .Select(o => o.ErrorMessage)
            .FirstOrDefault(msg => !string.IsNullOrWhiteSpace(msg));

        return (descriptors, lastError ?? "one or more rungs failed");
    }

    // ------------------------------------------------------------------
    // Dispatch helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Dispatches all decomposed children and re-enqueues the coordinator with
    /// phase state so it can poll for completion after a restart.
    /// </summary>
    /// <summary>
    /// Dispatches all decomposed children for the run. <paramref name="presetIds"/>
    /// is every preset id the decomposed <paramref name="tasks"/> cover — a
    /// single-element array for the legacy per-preset path, or every preset
    /// unioned into a smart-orchestrator merged run. Index 0 is always the
    /// primary preset (<see cref="CoordinatorState.PresetId"/>); the full
    /// array is only carried on <see cref="CoordinatorState.PresetIds"/> when
    /// there is more than one, so every existing single-preset serialized job
    /// keeps deserializing to exactly the shape it did before this array
    /// existed.
    /// </summary>
    private async Task DispatchDecomposedAsync(
        DecomposedTask[] tasks,
        OutputPlan plan,
        Ulid[] presetIds,
        FileMetadata fileMetadata,
        Stopwatch stopwatch,
        bool isPartialTopUp = false
    )
    {
        string groupTag = tasks[0].GroupTag;
        Ulid primaryPresetId = presetIds[0];
        Ulid[]? mergedPresetIds = presetIds.Length > 1 ? presetIds : null;

        Log.LogTrace(
            "[VideoEncodeJob] Decomposed into {Length} tasks (groupTag={GroupTag})",
            tasks.Length,
            groupTag
        );

        bool hasTwoPass = tasks.Any(task =>
            task.Kind == EncodeTaskKind.Pass1 || task.Kind == EncodeTaskKind.Pass2
        );

        string[] allTaskIds = tasks.Select(task => task.TaskId).ToArray();

        if (hasTwoPass)
        {
            // Two-pass: the Pass1 bundles run first (one per wake-up, gated by
            // resource budget); Pass2 + aux bundles wait in PendingBundles
            // until every Pass1 bundle has succeeded (see HandleWaitPass1Async).
            DecomposedTask[] pass1Bundles = tasks
                .Where(task => task.Kind == EncodeTaskKind.Pass1)
                .Select(task => task with { ParentJobId = _selfJobId })
                .ToArray();

            DecomposedTask[] pendingBundles = tasks
                .Where(task => task.Kind != EncodeTaskKind.Pass1)
                .Select(task => task with { ParentJobId = _selfJobId })
                .ToArray();

            Log.LogInformation(
                "[VideoEncodeJob] Planned {Length} Pass1 bundle(s). Transitioning to WaitPass1.",
                pass1Bundles.Length
            );

            ReEnqueueSelf(
                new(
                    GroupTag: groupTag,
                    TaskIds: allTaskIds,
                    Phase: CoordinatorPhase.WaitPass1,
                    Pass1DispatchedAt: DateTime.UtcNow,
                    Pass2DispatchedAt: null,
                    Pass1StatsPath: null,
                    PresetId: primaryPresetId,
                    ExpectedFinalCount: pendingBundles.Length,
                    Bundles: pass1Bundles,
                    CurrentBundleIndex: 0,
                    OutputDirectory: fileMetadata.Path,
                    PresetIds: mergedPresetIds,
                    PendingBundles: pendingBundles
                ),
                TimeSpan.Zero
            );
        }
        else
        {
            // Single-pass: pack tasks into resource-proportional bundles and
            // run ONE bundle at a time, each gated by its own resource
            // requirement. One ffmpeg in flight per source — never two
            // bundles racing the GPU / CPU / shared writes.
            DecomposedTask[] bundles = BuildResourceBundles(
                tasks,
                plan,
                _selfJobId,
                groupTag,
                isPartialTopUp
            );

            if (bundles.Length == 0)
            {
                Log.LogInformation("[VideoEncodeJob] No bundles produced — nothing to run.");
                return;
            }

            Log.LogInformation(
                "[VideoEncodeJob] Planned {Length} bundle(s) covering {Length2} streams. Sequential — bundle N+1 runs after bundle N succeeds. Transitioning to WaitChildren.",
                bundles.Length,
                tasks.Length
            );

            ReEnqueueSelf(
                new(
                    GroupTag: groupTag,
                    TaskIds: allTaskIds,
                    Phase: CoordinatorPhase.WaitChildren,
                    Pass1DispatchedAt: null,
                    Pass2DispatchedAt: null,
                    Pass1StatsPath: null,
                    PresetId: primaryPresetId,
                    ExpectedFinalCount: tasks.Length,
                    Bundles: bundles,
                    CurrentBundleIndex: 0,
                    OutputDirectory: fileMetadata.Path,
                    PresetIds: mergedPresetIds
                ),
                TimeSpan.Zero
            );
        }

        await Task.CompletedTask;
        _ = fileMetadata;
        _ = stopwatch;
    }

    /// <summary>
    /// Persists the coordinator's next phase and schedules the wake-up that will
    /// run it. The encode keeps ONE queue row from dispatch to finalize: the
    /// payload is rewritten in place, so the row's ID, creation time and queue
    /// position all survive every wake-up.
    ///
    /// <para>This used to enqueue a successor row and let the worker delete the
    /// original, which minted a fresh autoincrement ID roughly twice a minute.
    /// That ID is what the dashboard prints, what list keys are built from, and
    /// what the queue is ordered by — so a season of episodes renumbered and
    /// reshuffled itself on every poll. Carrying the original creation time onto
    /// the successor fixed the ordering but not the renumbering; only keeping the
    /// row fixes both, and it stops the next thing that reaches for a job ID from
    /// finding a value that will not hold still.</para>
    /// </summary>
    private void ReEnqueueSelf(CoordinatorState newState, TimeSpan availableAfter = default)
    {
        // Bump WakeSequence so the serialized payload differs from the row this
        // worker is currently processing. JobQueue.Enqueue dedups by Payload, and
        // the fallback path below still goes through it.
        CoordinatorState bumped = newState with
        {
            WakeSequence = newState.WakeSequence + 1,
        };

        VideoEncodeJob continueJob = new()
        {
            Id = Id,
            FolderId = FolderId,
            LibraryId = LibraryId,
            InputFile = InputFile,
            SourceDriverId = SourceDriverId,
            Status = "running",
            Coordinator = bumped,
        };

        JobQueue queue =
            QueueRunner.Current?.Queue
            ?? throw new InvalidOperationException(
                "QueueRunner.Current.Queue is null — queue not initialized"
            );

        string payload = SerializationHelper.Serialize(continueJob);

        if (_selfJobId > 0)
        {
            queue.UpdateJobPayload(_selfJobId, payload, availableAfter);
            RescheduledInPlace = true;
            return;
        }

        // No row to rewrite: this instance was never handed a queue-job ID, which
        // means it is not running under the worker (a direct in-process call, or a
        // test). Enqueueing keeps that path working; it just doesn't get a stable
        // ID, and nothing in that path is asking for one.
        queue.Enqueue(
            new()
            {
                Queue = QueueName,
                Payload = payload,
                Priority = Priority,
                AvailableAt = DateTime.UtcNow + availableAfter,
            }
        );

        // No re-enqueue trace — fires every coordinator tick (sub-second cadence
        // while bundles encode) and even at Verbose it floods the console. The
        // companion wake-up trace was already dropped for the same reason; real
        // phase transitions emit their own descriptive lines.
    }

    /// <summary>
    /// Pack ready single-pass tasks into decode-cost-driven bundles. Thin
    /// wrapper: resolves this host's GPU/CPU stream caps (Layer 2 capacity
    /// scheduling input, unchanged from before this refactor) then hands
    /// the actual grouping decision to <see cref="DecodeAwareBundlePlanner"/>
    /// — a plan-aware, unit-testable component that classifies every output
    /// by decode cost before applying host capacity.
    ///
    /// <para>A full ffmpeg decode is the real cost, not a rung count: a
    /// stream-copied output needs no decode at all, every plain transcode
    /// rung off the same source shares ONE decode via the filtergraph
    /// split the builder already emits, and every HDR→SDR tonemap rung
    /// shares its own single tonemap pass (and the thumbnail sprite) the
    /// same way. See <see cref="DecodeAwareBundlePlanner"/> for the full
    /// two-layer model.</para>
    /// </summary>
    private DecomposedTask[] BuildResourceBundles(
        DecomposedTask[] tasks,
        OutputPlan plan,
        int parentJobId,
        string groupTag,
        bool isPartialTopUp = false
    )
    {
        (int gpuCap, int cpuCap) = ResolveHostCaps(tasks);

        return DecodeAwareBundlePlanner.Plan(
            tasks,
            plan,
            parentJobId,
            groupTag,
            gpuCap,
            cpuCap,
            isPartialTopUp
        );
    }

    /// <summary>
    /// Resolve per-host bundle caps from <see cref="BundleCapResolver"/>.
    /// Caps are derived from real per-rung benchmark measurements
    /// (<see cref="IHardwareBenchmark"/> → <see cref="SpeedIndex"/>) on this
    /// exact host — no hardcoded model-name tiers, no driver-allowed
    /// maximums conflated with practical throughput. A weak GPU with a
    /// slow benchmark earns a smaller cap; a strong GPU earns a larger
    /// one. Driver-imposed session limits still apply as an outer ceiling.
    /// </summary>
    private (int GpuCap, int CpuCap) ResolveHostCaps(DecomposedTask[] tasks)
    {
        IHardwareBenchmark? benchmark = _hardwareBenchmark;
        IHardwareCapabilities? hardware = _hardwareCapabilities;

        BundleCapResolver.PlannedRung[] plannedRungs = tasks
            .Where(task =>
                task.Kind == EncodeTaskKind.Video
                && !string.IsNullOrEmpty(task.VideoEncoderName)
                && task.VideoWidth > 0
            )
            .Select(task => new BundleCapResolver.PlannedRung(
                Codec: InferCodec(task.VideoEncoderName!),
                EncoderName: task.VideoEncoderName!,
                Width: task.VideoWidth,
                IsGpu: task.Resources?.GpuDeviceKey is not null
            ))
            .ToArray();

        return BundleCapResolver.Resolve(plannedRungs, benchmark, hardware);
    }

    /// <summary>
    /// Derive the video codec family from an ffmpeg encoder name. Matches
    /// the convention used by <see cref="SpeedIndex"/> keys so a benchmark
    /// lookup hits the same row written by the calibration run.
    /// </summary>
    private static VideoCodecType InferCodec(string encoderName)
    {
        string lower = encoderName.ToLowerInvariant();
        if (lower.Contains("av1"))
            return VideoCodecType.Av1;
        if (lower.Contains("265") || lower.Contains("hevc"))
            return VideoCodecType.H265;
        if (lower.Contains("vp9"))
            return VideoCodecType.Vp9;
        return VideoCodecType.H264;
    }

    // ------------------------------------------------------------------
    // Inline path for Whole tasks (MP4, MKV — unchanged behavior)
    // ------------------------------------------------------------------

    private async Task RunInlineAsync(
        IEncodingOrchestrator orchestrator,
        EncodingRequest request,
        EncodingProfile encodingProfile,
        EncodingPreset preset,
        FileMetadata fileMetadata,
        Stopwatch stopwatch,
        IStorage sourceStorage,
        MediaContext context,
        FileManager fileManager,
        Folder folder
    )
    {
        IEncoderProcessRegistry? processRegistry = _encoderProcessRegistry;

        using EventBusProgressObserver progressObserver = new(
            jobId: fileMetadata.Id,
            title: fileMetadata.Title,
            baseFolder: fileMetadata.Path,
            sharePath: fileMetadata.Path,
            videoStreams: SummarizeVideo(encodingProfile),
            audioStreams: encodingProfile
                .Audio.Select(audio =>
                    $"{audio.Codec.ToString().ToLowerInvariant()} {audio.Channels}ch"
                )
                .ToList(),
            subtitleStreams: encodingProfile
                .Subtitles.Select(subtitle => subtitle.Codec.ToString().ToLowerInvariant())
                .ToList(),
            hasGpu: false,
            isHdr: false,
            registry: processRegistry,
            backdrop: fileMetadata.ImgPath
        );

        EncodingResult result = await orchestrator.EncodeAsync(
            request,
            progressObserver,
            _shutdownToken
        );

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Encoding failed for {InputFile}: {result.Error?.Message ?? "unknown error"}"
            );
        }

        Log.LogInformation(
            "Encoded {InputFile} → {OutputPath} in {TotalSeconds:F1}s ({Unknown})",
            InputFile,
            result.OutputPath,
            result.Duration.TotalSeconds,
            result.Metrics?.EncoderUsed ?? "unknown"
        );

        await PublishStageAsync(fileMetadata, "Recording encoding history");
        await RecordEncodingHistoryAsync(context, preset, result, InputFile, StorageDriver);

        await PublishStageAsync(fileMetadata, "Checking source subtitles");
        await RunBitmapSubtitleOcrAsync(
            fileMetadata,
            InputFile,
            sourceStorage,
            request.DestinationStorage ?? request.SourceStorage ?? sourceStorage
        );

        await PublishStageAsync(fileMetadata, "Refreshing library");
        await ScanEncodedOutputWithRetryAsync(
            fileManager,
            fileMetadata.Id,
            fileMetadata.Title,
            folder.FolderLibraries.First().Library,
            fileMetadata.FileName
        );

        if (EventBusProvider.IsConfigured)
        {
            stopwatch.Stop();
            await EventBusProvider.Current.PublishAsync(
                new EncodingCompletedEvent
                {
                    JobId = fileMetadata.Id,
                    OutputPath = result.OutputPath,
                    Duration = stopwatch.Elapsed,
                    PosterPath = fileMetadata.PosterPath,
                    BackdropPath = fileMetadata.ImgPath,
                }
            );
        }
    }

    // ------------------------------------------------------------------
    // Inline bundle execution — collapsed from the former EncodeTaskJob.
    // Every wake-up that reaches WaitPass1/WaitChildren with a bundle to run
    // arrives here already holding the resource-budget lease QueueWorker
    // acquired for THIS bundle's ResourceRequirement, so the ffmpeg command
    // runs right here, synchronously, inside the same Handle() call.
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs one decomposed bundle's ffmpeg command to completion, records its
    /// <see cref="EncodingHistory"/> row, and upserts its
    /// <see cref="EncodeTaskOutcome"/> row(s) — the same work the former
    /// <c>EncodeTaskJob.Handle()</c> did as a separate queue row. Returns the
    /// failure reason (if any) so the caller can surface it without a second
    /// round-trip to the outcomes table.
    /// </summary>
    private async Task<(bool Success, string? Error)> RunBundleInlineAsync(
        DecomposedTask task,
        Ulid presetId,
        Ulid[]? presetIds,
        string? outputDirectory
    )
    {
        await using MediaContext context = new();
        await using LibraryRepository libraryRepository = new(context, StorageDriver);

        Folder? folder = await libraryRepository.GetLibraryFolder(FolderId);
        if (folder is null)
            return (false, "folder not found");

        EncodingProfile encodingProfile;
        try
        {
            encodingProfile = PresetResolver.Resolve(presetId, new DbPresetLookup(context));
        }
        catch (Exception ex)
        {
            Log.LogWarning(
                "[VideoEncodeJob] Skipping bundle '{Label}' for preset {PresetId}: resolve failed — {Message}",
                task.Label,
                presetId,
                ex.Message
            );
            await WriteBundleOutcomeAsync(task, success: false, error: ex.Message, artifacts: []);
            return (false, ex.Message);
        }

        FileMetadata fileMetadata = await GetFileMetaData(folder, context);
        if (!fileMetadata.Success)
        {
            const string error = "Could not resolve media metadata";
            await WriteBundleOutcomeAsync(task, success: false, error: error, artifacts: []);
            return (false, error);
        }

        IEncodingOrchestrator orchestrator = _encodingOrchestrator!;

        IStorage destinationStorage = StorageFactory.For(folder.Id, folder.DriverId, folder.Path);
        IStorage sourceStorage = SourceStorageResolver.Resolve(
            StorageFactory,
            SourceDriverId,
            InputFile,
            folder,
            destinationStorage
        );

        EncodingRequest request = new(
            InputPath: InputFile,
            OutputDirectory: fileMetadata.Path,
            Profile: encodingProfile,
            MediaTitle: fileMetadata.FileName,
            SourceStorage: sourceStorage,
            DestinationStorage: destinationStorage,
            MediaItem: fileMetadata.MediaItem
        );

        // Smart-orchestrator merged run: task.OutputIndex indexes the MERGED
        // plan (union of every listed preset's video renditions). Re-plan and
        // re-merge every preset the same deterministic way the coordinator
        // did at decompose time, rather than carry the whole plan through
        // the serialized coordinator state.
        if (presetIds is { Length: > 1 })
        {
            List<EncodingRequest> mergeRequests = new(presetIds.Length);
            foreach (Ulid mergedPresetId in presetIds)
            {
                EncodingProfile presetProfile;
                try
                {
                    presetProfile = PresetResolver.Resolve(
                        mergedPresetId,
                        new DbPresetLookup(context)
                    );
                }
                catch (Exception ex)
                {
                    Log.LogWarning(
                        "[VideoEncodeJob] Merged bundle '{Label}': preset {PresetId} resolve failed — {Message}",
                        task.Label,
                        mergedPresetId,
                        ex.Message
                    );
                    await WriteBundleOutcomeAsync(
                        task,
                        success: false,
                        error: ex.Message,
                        artifacts: []
                    );
                    return (false, ex.Message);
                }

                mergeRequests.Add(request with { Profile = presetProfile });
            }

            OutputPlan? mergedPlan = await orchestrator.PlanMergedAsync(mergeRequests);
            if (mergedPlan is null)
            {
                string error =
                    $"Could not rebuild the merged plan for preset set [{string.Join(", ", presetIds)}]";
                Log.LogError(
                    "[VideoEncodeJob] Merged bundle '{Label}' failed: {Error}",
                    task.Label,
                    error
                );
                await WriteBundleOutcomeAsync(task, success: false, error: error, artifacts: []);
                return (false, error);
            }

            request = request with
            {
                Options = (request.Options ?? new EncodingOptions()) with
                {
                    PrecomputedPlan = mergedPlan,
                },
            };
        }

        // Propagate StatsFilePath from the task descriptor so TwoPassStrategyBase
        // receives the coordinator-resolved path for Pass2 bundles.
        if (!string.IsNullOrEmpty(task.StatsFilePath))
        {
            request = request with
            {
                Options = (request.Options ?? new EncodingOptions()) with
                {
                    StatsFilePath = task.StatsFilePath,
                },
            };
        }

        IEncoderProcessRegistry? processRegistry = _encoderProcessRegistry;

        using EventBusProgressObserver progressObserver = new(
            jobId: fileMetadata.Id,
            title: fileMetadata.Title,
            baseFolder: fileMetadata.Path,
            sharePath: fileMetadata.Path,
            registry: processRegistry,
            backdrop: fileMetadata.ImgPath
        );

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            EncodingResult result = await orchestrator.EncodeAsync(
                request,
                task,
                progressObserver,
                _shutdownToken
            );
            stopwatch.Stop();

            if (!result.Success)
            {
                string errorMsg =
                    result.Error?.Message ?? result.EnrichedError?.Message ?? "encode failed";
                Log.LogWarning(
                    "[VideoEncodeJob] Bundle '{Label}' failed: {ErrorMsg}",
                    task.Label,
                    errorMsg
                );
                await WriteBundleOutcomeAsync(task, success: false, error: errorMsg, artifacts: []);
                return (false, errorMsg);
            }

            Log.LogInformation(
                "[VideoEncodeJob] Bundle '{Label}' completed in {TotalSeconds:F1}s",
                task.Label,
                stopwatch.Elapsed.TotalSeconds
            );

            await RecordBundleHistoryAsync(
                context,
                task.Label,
                request.InputPath,
                encodingProfile,
                result
            );

            List<string> artifactPaths = [.. result.Artifacts.Select(artifact => artifact.Path)];
            await WriteBundleOutcomeAsync(
                task,
                success: true,
                error: null,
                artifacts: artifactPaths
            );
            return (true, null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.LogError(
                "[VideoEncodeJob] Bundle '{Label}' threw: {Message}",
                task.Label,
                ex.Message
            );
            await WriteBundleOutcomeAsync(task, success: false, error: ex.Message, artifacts: []);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Appends the dashboard-facing <see cref="EncodingHistory"/> row for one
    /// bundle. Best-effort: a failure here must not turn a completed encode
    /// into a failed one.
    /// </summary>
    private async Task RecordBundleHistoryAsync(
        MediaContext context,
        string label,
        string inputPath,
        EncodingProfile profile,
        EncodingResult result
    )
    {
        try
        {
            long inputBytes = result.Stats?.SourceBytes ?? 0;
            long outputBytes = result.Stats?.OutputBytes ?? 0;

            context.EncodingHistory.Add(
                new()
                {
                    InputPath = inputPath,
                    OutputPath = result.OutputPath,
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    EncoderUsed = result.Metrics?.EncoderUsed ?? string.Empty,
                    GpuUsed = result.Metrics?.GpuUsed,
                    DurationSeconds = result.Stats?.DurationSeconds ?? 0,
                    InputSizeBytes = inputBytes,
                    OutputSizeBytes = outputBytes,
                    CompressionRatio = inputBytes > 0 ? (double)outputBytes / inputBytes : 0,
                    AverageSpeed = result.Metrics?.AverageSpeed ?? 0,
                    AverageFps = result.Stats?.AvgFps ?? result.Metrics?.AverageFps ?? 0,
                }
            );
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Log.LogWarning(
                "[VideoEncodeJob] Could not record encoding history for bundle '{Label}': {Message}",
                label,
                ex.Message
            );
        }
    }

    /// <summary>
    /// Upserts the durable <see cref="EncodeTaskOutcome"/> row(s) for
    /// <paramref name="task"/> — one row per <see cref="DecomposedTask.BundledTaskIds"/>
    /// entry when the bundle covers more than one original stream-task, else
    /// one row for <see cref="DecomposedTask.TaskId"/> itself. Upsert, not
    /// insert-only: a queue-level retry of this same coordinator row must be
    /// able to overwrite a prior failed attempt's row and actually re-run the
    /// bundle, rather than finding a permanently "already recorded" failure
    /// and refusing to try again — there is no longer a separate child row
    /// with its own independent retry budget to fall back on.
    /// </summary>
    private async Task WriteBundleOutcomeAsync(
        DecomposedTask task,
        bool success,
        string? error,
        IReadOnlyList<string> artifacts
    )
    {
        string[] taskIds = task.BundledTaskIds is { Length: > 0 } bundled ? bundled : [task.TaskId];
        string? artifactsJson = artifacts.Count > 0 ? string.Join("\n", artifacts) : null;

        try
        {
            await using MediaContext outcomeContext = new();

            foreach (string taskId in taskIds)
            {
                EncodeTaskOutcome? existing =
                    await outcomeContext.EncodeTaskOutcomes.FirstOrDefaultAsync(row =>
                        row.TaskId == taskId
                    );

                if (existing is not null)
                {
                    existing.Success = success;
                    existing.ErrorMessage = error;
                    existing.OutputArtifactsJson = artifactsJson;
                    existing.CompletedAt = DateTime.UtcNow;
                }
                else
                {
                    outcomeContext.EncodeTaskOutcomes.Add(
                        new()
                        {
                            TaskId = taskId,
                            ParentJobId = task.ParentJobId,
                            GroupTag = task.GroupTag,
                            Success = success,
                            ErrorMessage = error,
                            Kind = task.Kind.ToString(),
                            OutputArtifactsJson = artifactsJson,
                            CompletedAt = DateTime.UtcNow,
                        }
                    );
                }
            }

            await outcomeContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Log.LogWarning(
                "[VideoEncodeJob] Failed to write outcome row(s) for bundle '{TaskId}': {Message}",
                task.TaskId,
                ex.Message
            );
        }

        if (!EventBusProvider.IsConfigured)
            return;

        await EventBusProvider.Current.PublishAsync(
            new EncodeTaskCompletedEvent
            {
                TaskId = task.TaskId,
                ParentJobId = task.ParentJobId,
                GroupTag = task.GroupTag,
                Success = success,
                Error = error,
                Kind = task.Kind,
                OutputArtifacts = artifacts,
            }
        );
    }

    // ------------------------------------------------------------------
    // Utility / helpers
    // ------------------------------------------------------------------

    private static List<string> SummarizeVideo(EncodingProfile profile)
    {
        List<string> summary = [];

        if (profile.Ladder?.Rungs is { Length: > 0 } rungs)
        {
            foreach (LadderRung rung in rungs)
                summary.Add(
                    $"{rung.Width}x{rung.Height} {rung.Codec.ToString().ToLowerInvariant()}"
                );
            return summary;
        }

        if (profile.Video is { } video)
            summary.Add(
                $"{video.Width}x{video.Height ?? 0} {video.Codec.ToString().ToLowerInvariant()}"
            );

        return summary;
    }

    private static async Task RecordEncodingHistoryAsync(
        MediaContext context,
        EncodingPreset preset,
        EncodingResult result,
        string inputPath,
        IStorageDriver storageDriver
    )
    {
        try
        {
            long inputSize = 0;
            try
            {
                if (storageDriver.FileExists(inputPath))
                    inputSize = storageDriver.GetFileSize(inputPath);
            }
            catch
            {
                // keep inputSize = 0 when the file is inaccessible
            }

            if (result.Metrics is null)
                return;

            double ratio =
                inputSize > 0 && result.Metrics.OutputSizeBytes > 0
                    ? (double)result.Metrics.OutputSizeBytes / inputSize
                    : 0;

            context.EncodingHistory.Add(
                new()
                {
                    InputPath = inputPath,
                    OutputPath = result.OutputPath,
                    ProfileId = preset.Id,
                    ProfileName = preset.Name,
                    EncoderUsed = result.Metrics.EncoderUsed,
                    GpuUsed = result.Metrics.GpuUsed,
                    DurationSeconds = result.Duration.TotalSeconds,
                    InputSizeBytes = inputSize,
                    OutputSizeBytes = result.Metrics.OutputSizeBytes,
                    CompressionRatio = ratio,
                    AverageSpeed = result.Metrics.AverageSpeed,
                    AverageFps = result.Metrics.AverageFps,
                }
            );
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Logger.Encoder(
                $"Could not write encoding history: {ex.Message}",
                LogEventLevel.Warning
            );
        }
    }

    // ------------------------------------------------------------------
    // Reconciliation — decide, before any ffmpeg command is built, what a
    // re-dispatch of an already-encoded file actually still needs to do.
    // ------------------------------------------------------------------

    /// <summary>
    /// Gathers what already exists for this (media, preset) combination and
    /// asks <see cref="IEncodeReconciler"/> what to do about it.
    /// <see cref="ForceFullReencode"/> short-circuits straight to Full
    /// without inspecting the destination at all — the operator escape
    /// hatch never pays the directory-listing / analysis cost either.
    /// </summary>
    private async Task<ReconciliationDecision> ReconcileAsync(
        EncodingProfile encodingProfile,
        FileMetadata fileMetadata,
        IStorage sourceStorage,
        IStorage destinationStorage
    )
    {
        IEncodeReconciler reconciler = _encodeReconciler!;

        if (ForceFullReencode)
            return reconciler.Decide(
                new(
                    encodingProfile,
                    IsSingleFileContainer(encodingProfile.Container),
                    BitmapSubtitleStreamCount: 0,
                    ExistingOutputSnapshot.Empty,
                    Force: true
                )
            );

        SourceReconciliationFacts source = await ProbeSourceForReconciliationAsync(sourceStorage);

        ExistingOutputSnapshot existing = await reconciler.InspectAsync(
            fileMetadata.Path,
            encodingProfile.Id.ToString(),
            destinationStorage,
            CancellationToken.None
        );

        return reconciler.Decide(
            new(
                encodingProfile,
                IsSingleFileContainer(encodingProfile.Container),
                source.BitmapSubtitleStreamCount,
                existing,
                SourceChapterCount: source.ChapterCount
            )
        );
    }

    /// <summary>
    /// What reconciliation needs to know about the SOURCE: how many bitmap
    /// (PGS/VobSub/DVB) subtitle streams <see cref="RunBitmapSubtitleOcrAsync"/>
    /// would OCR, and whether there are chapters for FinalizeStage to write.
    /// Both come from one analyzer call; the ffprobe pass is negligible next to
    /// the encode it lets reconciliation skip.
    /// </summary>
    private readonly record struct SourceReconciliationFacts(
        int BitmapSubtitleStreamCount,
        int ChapterCount
    );

    private async Task<SourceReconciliationFacts> ProbeSourceForReconciliationAsync(
        IStorage sourceStorage
    )
    {
        IMediaAnalyzer? analyzer = _mediaAnalyzer;
        if (analyzer is null)
            return new(0, 0);

        try
        {
            MediaInfo mediaInfo = await analyzer.AnalyzeAsync(
                InputFile,
                sourceStorage,
                CancellationToken.None
            );
            return new(
                mediaInfo.SubtitleStreams.Count(subtitle => !subtitle.IsTextBased),
                mediaInfo.Chapters.Count
            );
        }
        catch (Exception ex)
        {
            // Assuming none leaves reconciliation expecting nothing extra, which
            // degrades to "the output as it stands is complete" rather than to a
            // re-encode we cannot justify.
            Log.LogWarning(
                "Could not analyze {InputFile} for reconciliation — assuming no bitmap subtitles and no chapters: {Message}",
                InputFile,
                ex.Message
            );
            return new(0, 0);
        }
    }

    /// <summary>
    /// Single-file containers (MKV/MP4/audio-only) never decompose past a
    /// single Whole task — reconciliation for those can only ever be Skip or
    /// Full, never Partial-with-missing-kinds.
    /// </summary>
    private static bool IsSingleFileContainer(Container container) =>
        container
            is Container.Mkv
                or Container.Mp4
                or Container.Mp3
                or Container.Aac
                or Container.Flac
                or Container.Ogg
                or Container.Mka
                or Container.Mks;

    /// <summary>
    /// Runs when reconciliation finds every real output already valid and
    /// on-profile, with only the bitmap-subtitle OCR sidecar missing — the
    /// Frieren regression this whole reconciler exists to fix. No ffmpeg
    /// Build/Execute pass runs; only the lightweight OCR pass plus the
    /// post-encode library rescan.
    /// </summary>
    private async Task RunOcrTopUpAsync(
        FileMetadata fileMetadata,
        IStorage sourceStorage,
        IStorage destinationStorage,
        FileManager fileManager,
        Folder folder
    )
    {
        await PublishStageAsync(fileMetadata, "Converting subtitles");
        await RunBitmapSubtitleOcrAsync(fileMetadata, InputFile, sourceStorage, destinationStorage);

        await PublishStageAsync(fileMetadata, "Refreshing library");
        await ScanEncodedOutputWithRetryAsync(
            fileManager,
            fileMetadata.Id,
            fileMetadata.Title,
            folder.FolderLibraries.First().Library,
            fileMetadata.FileName
        );

        if (EventBusProvider.IsConfigured)
        {
            await EventBusProvider.Current.PublishAsync(
                new EncodingCompletedEvent
                {
                    JobId = fileMetadata.Id,
                    OutputPath = fileMetadata.Path ?? string.Empty,
                    Duration = TimeSpan.Zero,
                    PosterPath = fileMetadata.PosterPath,
                    BackdropPath = fileMetadata.ImgPath,
                }
            );
        }
    }

    private async Task RunBitmapSubtitleOcrAsync(
        FileMetadata fileMetadata,
        string inputPath,
        IStorage sourceStorage,
        IStorage destinationStorage
    )
    {
        IMediaAnalyzer? analyzer = _mediaAnalyzer;
        ISubtitleOcrEngine? ocrEngine = _subtitleOcrEngine;

        if (analyzer is null || ocrEngine is null)
            return;

        MediaInfo mediaInfo;
        try
        {
            mediaInfo = await analyzer.AnalyzeAsync(
                inputPath,
                sourceStorage,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            Log.LogWarning(
                "Could not analyze {InputPath} for OCR: {Message}",
                inputPath,
                ex.Message
            );
            return;
        }

        IReadOnlyList<BitmapSubtitleRef> bitmap = BitmapSubtitleSelector.Select(
            mediaInfo.SubtitleStreams
        );

        if (bitmap.Count == 0)
            return;

        // The encode's own output directory, addressed against the destination
        // storage rather than resolved to a local path: a remote destination
        // (NFS/S3) has none, and falling back to "next to the source" wrote the
        // sidecar to a driver-relative path that landed under the server's
        // working directory instead of the library.
        string ocrOutputDirectory = fileMetadata.Path;

        // Classified across every subtitle stream at once, never per stream: the
        // variant depends on a track's peers in the same language, and the
        // sidecar has to carry the same one as the .mks the extraction pass wrote
        // or the two never pair up.
        IReadOnlyList<string> variants = SubtitleClassifier.ResolveVariants(
            mediaInfo.SubtitleStreams
        );

        if (EventBusProvider.IsConfigured)
        {
            await EventBusProvider.Current.PublishAsync(
                new EncodingStageChangedEvent
                {
                    JobId = fileMetadata.Id,
                    Status = "running",
                    Title = fileMetadata.Title,
                    Message = "Converting subtitles",
                }
            );
        }

        foreach ((int subtitleIndex, SubtitleStreamInfo stream) in bitmap)
        {
            string language = stream.Language ?? "eng";

            try
            {
                SubtitleTrack track = await ocrEngine.OcrAsync(
                    inputPath,
                    subtitleIndex,
                    language,
                    SubtitleCodecType.WebVtt,
                    CancellationToken.None,
                    sourceStorage,
                    new OcrSidecarTarget(
                        Storage: destinationStorage,
                        OutputDirectory: ocrOutputDirectory,
                        MediaTitle: fileMetadata.FileName,
                        Variant: variants[subtitleIndex]
                    )
                );
                Log.LogInformation(
                    "OCR {Language} → {FilePath} ({CueCount} cues)",
                    language,
                    track.FilePath,
                    track.CueCount
                );
            }
            catch (Exception ex)
            {
                // Best-effort: OCR is a nice-to-have sidecar, never allowed to
                // fail an already-completed encode. ex.Message already carries
                // the real ffmpeg stderr tail (SubtitleOcrEngine embeds it), so
                // this warning stays actionable instead of a bare "OCR failed".
                Log.LogWarning(
                    "OCR failed for {InputPath} subtitle {SubtitleIndex} / abs stream {Index} ({Language}): {Message}",
                    inputPath,
                    subtitleIndex,
                    stream.Index,
                    language,
                    ex.Message
                );
            }
        }
    }

    private async Task<FileMetadata> GetFileMetaData(Folder folder, MediaContext context)
    {
        Movie? movie = folder.FolderLibraries.Any(x => x.Library.Type == MediaTypes.MovieMediaType)
            ? await context.Movies.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == Id.ToInt())
            : null;

        Episode? episode = folder.FolderLibraries.Any(x =>
            x.Library.Type == MediaTypes.TvMediaType || x.Library.Type == MediaTypes.AnimeMediaType
        )
            ? await context.Episodes.Include(x => x.Tv).FirstOrDefaultAsync(x => x.Id == Id.ToInt())
            : null;

        if (movie is null && episode is null)
            return new() { Success = false };

        string folderName =
            movie?.CreateFolderName().Replace("/", "")
            ?? episode!.Tv.CreateFolderName().Replace("/", "") + episode.CreateFolderName();

        string title = movie?.CreateTitle() ?? episode!.CreateTitle();
        string fileName = movie?.CreateFileName() ?? episode!.CreateFileName();
        string basePath = folderName;
        int baseId = movie?.Id ?? episode!.Id;
        string? imgPath = movie?.Backdrop ?? episode?.Still;
        string? posterPath = movie?.Poster ?? episode?.Tv.Poster;
        MediaItemRef mediaItem = MediaItemRefFactory.Create(movie, episode);

        return new()
        {
            Success = true,
            FolderName = folderName,
            Title = title,
            FileName = fileName,
            Path = basePath,
            Id = baseId,
            ImgPath = imgPath,
            PosterPath = posterPath,
            MediaItem = mediaItem,
        };
    }

    /// <summary>
    /// Builds the reconstruction-metadata reference for the movie or episode
    /// being encoded. <paramref name="movie"/> and <paramref name="episode"/>
    /// are mutually exclusive — exactly one is non-null (callers already
    /// verified that before reaching this point).
    /// </summary>
    private record FileMetadata
    {
        public bool Success { get; set; }
        public string FolderName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public int Id { get; set; }
        public string? ImgPath { get; set; }
        public string? PosterPath { get; set; }

        /// <summary>
        /// The resolved movie/episode reference for reconstruction-metadata
        /// wiring. Set on every <see cref="EncodingRequest"/> this job builds —
        /// the inline Whole-task request in <see cref="HandleInitialRunAsync"/>
        /// and the coordinator's FinalizeOnly request in
        /// <see cref="HandleFinalizeAsync"/> alike. Pure identity: it can never
        /// change the ffmpeg command that produced the output, because that is
        /// gated separately by <see cref="EncodingOptions.EnableMetadataInjection"/>,
        /// which this job never sets.
        /// </summary>
        public MediaItemRef? MediaItem { get; set; }
    }

    private static async Task PublishStageAsync(FileMetadata fileMetadata, string message)
    {
        if (!EventBusProvider.IsConfigured)
            return;
        await EventBusProvider.Current.PublishAsync(
            new EncodingStageChangedEvent
            {
                JobId = fileMetadata.Id,
                Status = "running",
                Title = fileMetadata.Title,
                Message = message,
            }
        );
    }
}
