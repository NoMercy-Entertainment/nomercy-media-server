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

using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Naming;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Progress;
using NoMercy.Storage;

namespace NoMercy.Encoder.Pipeline;

public interface IEncoder
{
    Task<EncodingResult> EncodeAsync(
        EncodingRequest request,
        IProgressObserver? progress = null,
        CancellationToken ct = default
    );

    Task<PreviewResult> PreviewAsync(
        EncodingRequest request,
        int previewDurationSeconds = 10,
        CancellationToken ct = default
    );

    /// <summary>
    /// Run Analyze → Validate → Plan stages only (no ffmpeg process).
    /// Returns the <see cref="OutputPlan"/> so callers can call
    /// <see cref="IEncodingStrategy.Decompose"/> without running the full encode.
    /// Returns null when any stage fails.
    /// </summary>
    Task<OutputPlan?> PlanAsync(EncodingRequest request, CancellationToken ct = default);
}

public record EncodingRequest(
    string InputPath,
    string OutputDirectory,
    EncodingProfile Profile,
    EncodingOptions? Options = null,
    string? MediaTitle = null,
    /// <summary>
    /// Per-folder storage for the source file. When null the encoder falls
    /// back to the app-level singleton injected via DI. Set by encode jobs
    /// to the folder-scoped IStorage resolved by IStorageFactory so path
    /// guards and remote-driver routing are applied correctly.
    /// </summary>
    IStorage? SourceStorage = null,
    /// <summary>
    /// Per-folder storage for the encode output directory. Defaults to
    /// SourceStorage when null and SourceStorage is set; otherwise falls
    /// back to the DI singleton.
    /// When source and destination folders differ (e.g. source on SMB,
    /// output on local SSD) pass separate instances here.
    /// </summary>
    IStorage? DestinationStorage = null,
    /// <summary>
    /// The library item being encoded (movie or episode). Safe to set on EVERY
    /// request — including ones where Build/Execute run — because it is pure
    /// identity and has no effect on the emitted ffmpeg command. Drives
    /// <see cref="Naming.BundleLayout"/> resolution in PlanStage and the
    /// manifest.json / reconstruction.json writes in FinalizeStage. Null (the
    /// default) preserves today's behavior exactly: no layout, no reconstruction
    /// artifacts — the source has no resolvable library item (e.g. a disc rip).
    /// See <see cref="EncodingOptions.EnableMetadataInjection"/> for the separate
    /// opt-in that actually changes the command.
    /// </summary>
    MediaItemRef? MediaItem = null,
    /// <summary>
    /// The caller's real source, when <see cref="InputPath"/> has been rewritten
    /// to a local staging lease. The lease is deleted once the encode finishes,
    /// so anything describing what was encoded — reconstruction.json above all —
    /// has to name this instead. Null when no staging happened and InputPath is
    /// already the real thing.
    /// </summary>
    string? OriginalInputPath = null,
    /// <summary>
    /// The caller's real destination, when <see cref="OutputDirectory"/> has been
    /// rewritten to a staging directory. Same reasoning as
    /// <see cref="OriginalInputPath"/>: the staging directory is emptied into the
    /// destination and removed, so a manifest naming it describes a place that no
    /// longer exists. Null when no staging happened.
    /// </summary>
    string? OriginalOutputDirectory = null
)
{
    /// <summary>
    /// Title used for output file naming (master playlist, subtitles).
    /// When not set, derived from the output directory leaf name + ".NoMercy".
    /// </summary>
    public string ResolvedTitle =>
        MediaTitle
        ?? $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(OutputDirectory))}.NoMercy";

    /// <summary>Host shutdown token: a cancel from it keeps the working folder.</summary>
    public CancellationToken HostStopping { get; init; }
}

public record EncodingOptions(
    bool ResumeFromCheckpoint = false,
    int? MaxConcurrentEncodes = null,
    Priority Priority = Priority.Normal,
    EncodingPass Pass = EncodingPass.Single,
    string? StatsFilePath = null,
    int Pass1VariantIndex = 0,
    /// <summary>
    /// When set, restricts the BuildStage to emit commands only for the
    /// outputs described by this task. Null means "emit everything" (normal
    /// full-plan execution). Set by the orchestrator when running a
    /// single decomposed child task.
    /// </summary>
    DecomposedTask? TaskFilter = null,
    /// <summary>
    /// When true the pipeline runs Analyze → Validate → Plan → Finalize,
    /// skipping Build + Execute. Used by the encode coordinator after all
    /// decomposed child tasks have written their slices to the shared
    /// tempDir — the coordinator then runs FinalizeStage once against the
    /// existing variant playlists / segments to produce the master m3u8,
    /// chapters.vtt, fonts.json, and bundle manifest. Concurrent per-task
    /// FinalizeStage runs would race those shared outputs and corrupt
    /// them, so per-task encodes leave FinalizeStage to this flag.
    /// </summary>
    bool FinalizeOnly = false,
    /// <summary>
    /// When non-null, BuildStage applies an input seek (<c>-ss</c>) to the
    /// primary input at the keyframe immediately before this position. Used
    /// by the resume path to restart the encode from near the last-known
    /// good position rather than from the beginning.
    /// </summary>
    long? ResumeFromMs = null,
    /// <summary>
    /// Explicit opt-in for BuildStage to splice -metadata / per-stream metadata /
    /// disposition args into the main ffmpeg command from
    /// <see cref="EncodingRequest.MediaItem"/>. Deliberately independent of whether
    /// MediaItem is set — MediaItem is pure identity (safe everywhere); this flag
    /// is the only thing allowed to change the command. Defaults to false so every
    /// existing production request keeps emitting the exact command it does today.
    /// </summary>
    bool EnableMetadataInjection = false,
    /// <summary>
    /// When set, the pipeline skips PlanStage entirely and uses this plan
    /// instead of re-deriving one from <see cref="EncodingRequest.Profile"/>.
    /// Set by the smart-orchestrator merge path
    /// (<see cref="Orchestration.IEncodingOrchestrator.DecomposeMergedAsync"/>)
    /// so every child task and the coordinator's FinalizeOnly pass all build
    /// their ffmpeg command / master playlist against the SAME merged
    /// <see cref="Output.OutputPlan"/> — one that unions the video renditions
    /// of every preset in the run — instead of each independently re-deriving
    /// a plan from just its own preset's profile. Null (default) preserves
    /// today's behavior exactly: PlanStage runs and derives the plan from
    /// the single request Profile.
    /// </summary>
    Output.OutputPlan? PrecomputedPlan = null
);

public enum Priority
{
    Normal,
    High,
}

/// <summary>
/// Which pass of a 2-pass encode this call represents. <see cref="Single"/>
/// is the default — the pipeline emits normal HLS output without any `-pass`
/// flag. <see cref="One"/> does video-only analysis writing to a stats file
/// (no HLS / audio / subtitle / sprite outputs). <see cref="Two"/> reads the
/// stats file and produces the final HLS encode with `-pass 2`.
/// </summary>
public enum EncodingPass
{
    Single,
    One,
    Two,
}

// EncodingResult and EncodingMetrics are defined in
// Orchestration/EncodingResult.cs (namespace NoMercy.Encoder.Pipeline)
// so both the pipeline and orchestration layers share the same type
// without a circular reference.
