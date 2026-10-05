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

using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Metadata;
using NoMercy.Encoder.Naming;
using NoMercy.Storage;

namespace NoMercy.Encoder.Pipeline;

/// <summary>
/// Per-encode context threaded through every <see cref="IPipelineStage{TIn, TOut}"/>.
/// Carries the correlation ID, the analysed source metadata once available,
/// per-folder storage instances, and the <see cref="IDecisionLogSink"/> the
/// stages append to so the dashboard can render the per-job decision trace.
/// </summary>
/// <param name="CorrelationId">Stable per-encode ID used for log correlation.</param>
/// <param name="MediaInfo">Set by AnalyzeStage on success; null until then.</param>
/// <param name="Decisions">Sink for <see cref="DecisionLog"/> entries. Defaults to null (= no-op via <see cref="DecisionsOrNoOp"/>) so legacy callers don't have to opt in.</param>
/// <param name="SourceStorage">
/// Per-folder storage for the source input file. When null the stage falls
/// back to the app-level singleton injected through DI.
/// </param>
/// <param name="DestinationStorage">
/// Per-folder storage for the encode output directory. When null the stage
/// falls back to SourceStorage (if set) or the DI singleton. Cross-backend
/// encodes (e.g. NFS source, S3 destination) are handled by EncodingOrchestrator
/// which stages the source via <c>AcquireLocalPathAsync</c> and stream-writes
/// outputs to the destination storage.
/// </param>
public record EncodingContext(
    string CorrelationId,
    MediaInfo? MediaInfo = null,
    IDecisionLogSink? Decisions = null,
    IStorage? SourceStorage = null,
    IStorage? DestinationStorage = null,
    /// <summary>
    /// The library item being encoded (movie or episode). Pure identity — drives
    /// <see cref="Naming.OutputNamingResolver.Resolve"/> → <see cref="Naming.BundleLayout"/>
    /// → the manifest.json / reconstruction.json writes in FinalizeStage. Safe to
    /// set on EVERY request, including ones where Build/Execute run, because it no
    /// longer has any effect on the emitted ffmpeg command — see
    /// <see cref="EnableMetadataInjection"/> for the separate opt-in that controls
    /// that. Null (the default) preserves today's behavior exactly: no layout, no
    /// reconstruction artifacts — used when the source has no resolvable library
    /// item (e.g. a disc rip).
    /// </summary>
    MediaItemRef? MediaItem = null,
    /// <summary>
    /// Explicit opt-in for BuildStage to splice -metadata / per-stream metadata /
    /// disposition args (via <see cref="Metadata.MetadataInjector"/>) into the main
    /// ffmpeg command. Deliberately separate from <see cref="MediaItem"/> — MediaItem
    /// is pure identity and must be safe to set on every request; this flag is the
    /// only thing allowed to change the command. Defaults to false so every existing
    /// production request keeps emitting the exact command it does today.
    /// </summary>
    bool EnableMetadataInjection = false,
    /// <summary>
    /// Source-file stream metadata for copy-mode encodes. Populated by the
    /// caller (typically the job that dispatches the encode) from the
    /// MediaInfo stream objects. When null the merger falls back to DB-only.
    /// </summary>
    IReadOnlyList<SourceTrackMetadata>? SourceTracks = null,
    /// <summary>
    /// DB-side per-stream metadata rows. When set and SourceTracks is also set
    /// and the encode is copy-mode, BuildStage runs MetadataMerger before
    /// passing the result to MetadataInjector.
    /// </summary>
    IReadOnlyList<TrackMetadata>? DbTracks = null,
    string? OutputDirectory = null,
    string? InputPath = null,
    /// <summary>
    /// The real source, when <see cref="InputPath"/> points at a staging lease
    /// that will be deleted after the encode. Null when nothing was staged.
    /// </summary>
    string? OriginalInputPath = null,
    /// <summary>
    /// The real destination, when <see cref="OutputDirectory"/> points at a
    /// staging directory that gets published and removed. Null when nothing was
    /// staged.
    /// </summary>
    string? OriginalOutputDirectory = null,
    /// <summary>
    /// The decomposed task's own resource reservation (see
    /// <see cref="NoMercyQueue.Core.Resources.IHasResourceRequirement"/>), when this
    /// run is one child of a coordinator. PlanStage sizes the ffmpeg <c>-threads</c>
    /// flag from this instead of a fixed share of the host, so a task never asks
    /// ffmpeg for more CPU than <see cref="NoMercy.Encoder.Hardware.ResourceBudget"/>
    /// actually reserved for it — the mismatch between the two let N budget-compliant
    /// concurrent tasks each spawn a full-width ffmpeg process and peg the host. Null
    /// for whole-file runs with no coordinator (preview, live-transcode) — those keep
    /// the flat host-fraction default.
    /// </summary>
    ResourceRequirement? Resources = null
)
{
    /// <summary>
    /// Always non-null — falls back to a no-op sink when the caller
    /// passed nothing for <see cref="Decisions"/>.
    /// </summary>
    public IDecisionLogSink DecisionsOrNoOp => Decisions ?? NullDecisionLogSink.Instance;

    public static EncodingContext Create() =>
        new(CorrelationId: Ulid.NewUlid().ToString(), Decisions: new ScopedDecisionLog());
}
