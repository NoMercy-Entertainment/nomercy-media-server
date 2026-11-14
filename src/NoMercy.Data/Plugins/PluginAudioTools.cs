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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Startup;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Quotas;
using NoMercy.Storage;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Runs the server's own ffmpeg build on behalf of one plugin - the host side
/// of <see cref="IPluginAudioTools" />.
/// <para>
/// Every rejection comes back as a refusal in words rather than as an
/// exception, for the same reason the analysis writer does it: a plugin's
/// sweep runs unattended, and a stack trace nobody reads is worth nothing
/// next to a reason the owner can act on. That holds for failures nobody
/// planned for too - both public members catch what they did not expect, log
/// it at Warning and refuse with the exception's type name. Cancellation the
/// caller asked for is the one thing that still propagates.
/// </para>
/// <para>
/// One instance per plugin, and one ffmpeg at a time inside it. A plugin that
/// fans out a library sweep would otherwise start one ffmpeg per track and
/// take the owner's machine down with work it is not even watching; the
/// second call is refused rather than queued, so the plugin decides what to
/// do about it instead of accumulating an invisible backlog.
/// </para>
/// </summary>
public sealed class PluginAudioTools(
    Ulid pluginId,
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorage storage,
    IStorageDriver storageDriver,
    IStorage derivedStorage,
    IDerivedAudioStore store,
    IPluginMusicAnalysisWriterFactory writerFactory,
    IDbContextFactory<MediaContext> contextFactory,
    IFfmpegCapabilityProbe capabilityProbe,
    ILogger<PluginAudioTools>? logger = null,
    string? dataFolderPath = null,
    PluginQuotaMeter? quotas = null,
    ILyricsAlignModelInstaller? alignModelInstaller = null
) : IPluginAudioTools
{
    /// <summary>
    /// Matches the analysis pass: ffmpeg on a stalled mount never returns on
    /// its own.
    /// <para>
    /// Init-only and internal rather than a constant so the tests can prove
    /// the timeout path without waiting ten minutes for it. Every production
    /// call site takes the default; nothing can change it after construction,
    /// so no two instances ever disagree about their own timeout mid-run.
    /// </para>
    /// </summary>
    internal TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a run is given to exit on its own after its stdout carries
    /// ffmpeg's own "progress=end" line before the host ends it - see
    /// <see cref="RunFfmpegAsync" />.
    /// <para>
    /// Init-only for the same reason as <see cref="RunTimeout" />: so a test
    /// can prove the path without waiting for it.
    /// </para>
    /// </summary>
    internal TimeSpan ExitGracePeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often a render's files are measured against the plugin's disk
    /// quota while ffmpeg is still writing them. ffmpeg writes the bytes
    /// itself, so the server cannot refuse one before it lands the way it
    /// does for a write through the storage facade; it can only look, and
    /// this is how long a run that outgrew the quota keeps writing at most.
    /// <para>Init-only for the same reason as <see cref="RunTimeout" />.</para>
    /// </summary>
    internal TimeSpan QuotaCheckInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The shortest stall limit a render is held to, whatever the plugin
    /// asked for - see <see cref="PluginAudioRunLimits.ShortestStall" />.
    /// <para>Init-only for the same reason as <see cref="RunTimeout" />.</para>
    /// </summary>
    internal TimeSpan ShortestStallLimit { get; init; } = PluginAudioRunLimits.ShortestStall;

    /// <summary>The clock a render's limits and its exit grace period count on.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The derived store's own scratch folder; its eviction sweep also cleans it.</summary>
    private const string TempFolder = "tmp";

    // Format and content type live in StemFormats; only the sample rate is
    // this encoder's own.
    private const int OpusSampleRate = 48000;

    /// <summary>The share of a track a mix into it draws from.</summary>
    private const double MixInShare = 0.20;

    /// <summary>Where the window a mix out of a track draws from starts.</summary>
    private const double MixOutStart = 0.75;

    private const string FfmpegMissing = "ffmpeg is not installed";
    private const string StemsplitModelMissing = "the stemsplit model is not installed";
    private const string WhisperModelMissing = "the whisper model is not installed";
    private const string VadModelMissing = "the VAD model is not installed";
    private const string AlignFilterMissing =
        "this server's ffmpeg has no lyricsalign filter; it needs nomercy-ffmpeg v1.0.46 or later";
    private const string AlignModelMissing = "the lyricsalign model is not installed";
    private const string AlignModelOnItsWay =
        "the lyricsalign model is not installed yet; the server has started downloading it, ask again in a few minutes";
    private const string EspeakDataMissing =
        "the espeak-ng data is missing from the ffmpeg folder; it ships with nomercy-ffmpeg v1.0.46 and later";
    private const string RunInProgress = "another ffmpeg run of this plugin is still in progress";
    private const string NoStemOutput = "stemsplit produced no output";

    private readonly SemaphoreSlim _oneRunAtATime = new(1, 1);

    private readonly ILogger<PluginAudioTools> _logger =
        logger ?? NullLogger<PluginAudioTools>.Instance;

    public Task<PluginAudioRunResult> RunFilterGraphAsync(
        PluginAudioInput input,
        PluginFilterGraph graph,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        CancellationToken ct = default
    ) =>
        PluginCallGuard.RunAsync(
            pluginId.ToString(),
            nameof(RunFilterGraphAsync),
            () => RunFilterGraphCoreAsync(input, graph, onStdOut, onStdErr, null, ct),
            PluginAudioRunResult.Refused,
            _logger
        );

    public Task<PluginAudioRunResult> RunFilterGraphAsync(
        PluginAudioInput input,
        PluginFilterGraph graph,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        PluginAudioRunOptions options,
        CancellationToken ct = default
    ) =>
        PluginCallGuard.RunAsync(
            pluginId.ToString(),
            nameof(RunFilterGraphAsync),
            () => RunFilterGraphCoreAsync(input, graph, onStdOut, onStdErr, options, ct),
            PluginAudioRunResult.Refused,
            _logger
        );

    /// <summary>
    /// The timeout a run gets: the host's default without options, what the
    /// plugin asked for otherwise, never more than the maximum. A zero or
    /// negative request is a mistake, not a wish for no time at all.
    /// </summary>
    internal static TimeSpan EffectiveTimeout(PluginAudioRunOptions? options, TimeSpan fallback) =>
        options?.Timeout is { } requested && requested > TimeSpan.Zero
            ? requested > PluginAudioRunOptions.MaxTimeout
                ? PluginAudioRunOptions.MaxTimeout
                : requested
            : fallback;

    private async Task<PluginAudioRunResult> RunFilterGraphCoreAsync(
        PluginAudioInput input,
        PluginFilterGraph graph,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        PluginAudioRunOptions? options,
        CancellationToken ct
    )
    {
        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            return PluginAudioRunResult.Refused(FfmpegMissing);
        }

        if (MissingModel(graph.Text, everyStemsplitNeedsTheServersModel: true) is { } missingModel)
        {
            return PluginAudioRunResult.Refused(missingModel);
        }

        ResolvedGraphText resolvedText = PluginGraphTokens.Resolve(graph.Text, dataFolderPath);
        if (resolvedText.Refusal is not null)
        {
            return PluginAudioRunResult.Refused(resolvedText.Refusal);
        }

        string text = resolvedText.Text;

        if (!await _oneRunAtATime.WaitAsync(0, ct))
        {
            return PluginAudioRunResult.Refused(RunInProgress);
        }

        try
        {
            InputResolution resolved = await ResolveInputAsync(input, ct);
            if (resolved.Refusal is not null)
            {
                return PluginAudioRunResult.Refused(resolved.Refusal);
            }

            await using LocalPathLease lease = resolved.Lease!;

            string[] arguments = PluginAudioArguments.FilterGraph(lease.Path, text, graph.Complex);

            ProcessResult? result = await RunFfmpegAsync(
                ffmpegPath,
                arguments,
                onStdOut,
                onStdErr,
                ct,
                timeout: EffectiveTimeout(options, RunTimeout)
            );

            // Null is the timeout: -2 rather than a refusal, because the run
            // did start - the plugin's graph was accepted and its callbacks
            // saw whatever ffmpeg managed to print before the clock ran out.
            return result is null
                ? new PluginAudioRunResult(PluginAudioRunResult.TimedOutExitCode, null)
                : new PluginAudioRunResult(result.ExitCode, null);
        }
        finally
        {
            _oneRunAtATime.Release();
        }
    }

    public Task<PluginAudioRunResult> RenderAsync(
        PluginAudioRender render,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        PluginAudioRunOptions options,
        CancellationToken ct = default
    ) =>
        PluginCallGuard.RunAsync(
            pluginId.ToString(),
            nameof(RenderAsync),
            () => RenderCoreAsync(render, onStdOut, onStdErr, options, ct),
            PluginAudioRunResult.Refused,
            _logger
        );

    private async Task<PluginAudioRunResult> RenderCoreAsync(
        PluginAudioRender render,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        PluginAudioRunOptions? options,
        CancellationToken ct
    )
    {
        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            return PluginAudioRunResult.Refused(FfmpegMissing);
        }

        // Everything the request itself can be refused for is settled here,
        // before the lock is taken, a file is opened or a folder is made: a
        // path that leaves the plugin's folder never gets as far as ffmpeg.
        PluginAudioRenderPlan plan = PluginAudioRenderPlan.Build(
            render,
            dataFolderPath,
            storageDriver
        );
        if (plan.Refusal is not null)
        {
            return PluginAudioRunResult.Refused(plan.Refusal);
        }

        if (
            MissingModel(render.GraphText, everyStemsplitNeedsTheServersModel: false) is
            { } missingModel
        )
        {
            return PluginAudioRunResult.Refused(missingModel);
        }

        ResolvedGraphText graphText = PluginGraphTokens.Resolve(render.GraphText, dataFolderPath);
        if (graphText.Refusal is not null)
        {
            return PluginAudioRunResult.Refused(graphText.Refusal);
        }

        if (!await _oneRunAtATime.WaitAsync(0, ct))
        {
            return PluginAudioRunResult.Refused(RunInProgress);
        }

        try
        {
            return await RenderInsideTheLockAsync(
                ffmpegPath,
                render,
                plan,
                graphText.Text,
                onStdOut,
                onStdErr,
                options,
                ct
            );
        }
        finally
        {
            _oneRunAtATime.Release();
        }
    }

    /// <summary>
    /// The render itself, with the one-ffmpeg-per-plugin lock already held:
    /// open the inputs, run ffmpeg into temporary files, and keep those files
    /// only when the run exited 0 and they fit the plugin's quota.
    /// </summary>
    private async Task<PluginAudioRunResult> RenderInsideTheLockAsync(
        string ffmpegPath,
        PluginAudioRender render,
        PluginAudioRenderPlan plan,
        string graphText,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        PluginAudioRunOptions? options,
        CancellationToken ct
    )
    {
        PluginAudioRenderFiles files = new(
            pluginId,
            dataFolderPath!,
            plan.Targets,
            storageDriver,
            _logger
        );
        List<LocalPathLease> leases = [];

        try
        {
            List<PluginAudioRenderInput> inputs = [];
            foreach (PluginAudioSource source in render.Inputs)
            {
                InputResolution resolved = await ResolveInputAsync(source.Input, ct);
                if (resolved.Refusal is not null)
                {
                    return PluginAudioRunResult.Refused(resolved.Refusal);
                }

                leases.Add(resolved.Lease!);
                inputs.Add(new(resolved.Lease!.Path, source.Start, source.Duration));
            }

            files.Prepare();

            // Every reason this run can be ended for goes through one token,
            // the caller's own cancellation included: whatever ends it, the
            // process has to be gone before its files are removed below.
            PluginAudioRunLimits limits = PluginAudioRunLimits.ForRender(
                options,
                RunTimeout,
                ShortestStallLimit
            );
            using PluginAudioRunWatch watch = new(limits, Clock);
            using CancellationTokenSource pastQuota = new();
            using CancellationTokenSource endRun = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                watch.Stalled,
                watch.TimedOut,
                pastQuota.Token
            );
            using CancellationTokenSource runIsOver = new();

            void ObserveStdOut(string line)
            {
                watch.Observe(line);
                onStdOut?.Invoke(line);
            }

            Task<PluginRefusal?> quotaWatch = WatchDiskQuotaAsync(
                files,
                pastQuota,
                runIsOver.Token
            );
            ProcessResult? result;
            PluginRefusal? quotaRefusal;

            try
            {
                watch.Start();
                result = await RunFfmpegAsync(
                    ffmpegPath,
                    PluginAudioArguments.Render(inputs, graphText, plan.Targets),
                    ObserveStdOut,
                    onStdErr,
                    ct,
                    watchForExitMarker: true,
                    // The watch keeps this run's total, next to its stall
                    // limit, so both end it the same way.
                    timeout: Timeout.InfiniteTimeSpan,
                    endRun: endRun.Token
                );
            }
            finally
            {
                // Awaited on every way out, a cancellation included: the
                // watch holds the token sources this method is about to
                // dispose, and must be done with them first.
                await runIsOver.CancelAsync();
                quotaRefusal = await quotaWatch;
            }

            if (quotaRefusal is not null)
            {
                return PluginAudioRunResult.Refused(PastTheQuota(quotaRefusal));
            }

            // Null is the host ending the run. Not a refusal either way,
            // because the run did start: the plugin's graph was accepted and
            // its callbacks saw whatever ffmpeg printed until then.
            if (result is null)
            {
                if (!watch.HasStalled)
                {
                    _logger.LogWarning(
                        "plugin {PluginId}: render timed out after {Seconds}s",
                        pluginId,
                        limits.Total.TotalSeconds
                    );
                    return new(PluginAudioRunResult.TimedOutExitCode, null);
                }

                _logger.LogWarning(
                    "plugin {PluginId}: render made no progress for {Seconds}s and was ended",
                    pluginId,
                    limits.Stall?.TotalSeconds
                );
                return new(PluginAudioRunResult.StalledExitCode, null);
            }

            return result.ExitCode == 0
                ? KeepRenderedFiles(files)
                : new PluginAudioRunResult(result.ExitCode, null);
        }
        finally
        {
            // Every way out that did not keep the files would otherwise leave
            // one that looks finished. After a kept run there is nothing left
            // under a temporary name, and this finds nothing to do.
            files.Cleanup();

            foreach (LocalPathLease lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Gives the files of a run that exited 0 their real names, once they
    /// are all there and fit the plugin's disk quota.
    /// </summary>
    private PluginAudioRunResult KeepRenderedFiles(PluginAudioRenderFiles files)
    {
        if (files.PadWithoutAFile() is { } pad)
        {
            _logger.LogWarning(
                "plugin {PluginId}: render exited 0 but wrote no file for pad {Pad}",
                pluginId,
                pad
            );
            return PluginAudioRunResult.Refused($"the render wrote no file for pad '{pad}'");
        }

        // Counted before the files get their names, so a render that does not
        // fit is refused while nothing in the folder has changed yet. A render
        // that replaces more than it writes gives the difference back instead.
        long net = files.NetBytes();
        if (net > 0 && quotas?.AccountDisk(pluginId, net) is { } refusal)
        {
            return PluginAudioRunResult.Refused(PastTheQuota(refusal));
        }

        try
        {
            files.Commit();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (net > 0)
            {
                quotas?.ReleaseDisk(pluginId, net);
            }

            _logger.LogWarning(
                exception,
                "plugin {PluginId}: render finished but its files could not be put in place",
                pluginId
            );
            return PluginAudioRunResult.Refused(
                "the rendered files could not be put in place; a file they replace may still be open"
            );
        }

        if (net < 0)
        {
            quotas?.ReleaseDisk(pluginId, -net);
        }

        return new(0, null) { Outputs = files.Rendered() };
    }

    /// <summary>
    /// Looks at what a running render has written so far, and ends the run
    /// once that no longer fits the plugin's disk quota. Returns the refusal
    /// when it did, null when the run ended inside the quota or the host
    /// meters no disk at all.
    /// <para>
    /// A graph can write far faster than it reads - a generator has no input
    /// to wait for - so a quota that was only asked about afterwards would
    /// let one run fill the disk first.
    /// </para>
    /// </summary>
    private async Task<PluginRefusal?> WatchDiskQuotaAsync(
        PluginAudioRenderFiles files,
        CancellationTokenSource pastQuota,
        CancellationToken runIsOver
    )
    {
        if (quotas is null)
        {
            return null;
        }

        using PeriodicTimer timer = new(QuotaCheckInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(runIsOver))
            {
                if (quotas.CheckDisk(pluginId, files.NetBytes()) is not { } refusal)
                {
                    continue;
                }

                _logger.LogWarning(
                    "plugin {PluginId}: render outgrew the plugin's disk quota and was ended",
                    pluginId
                );
                await pastQuota.CancelAsync();
                return refusal;
            }
        }
        catch (OperationCanceledException)
        {
            // The run is over; there is nothing left to watch.
        }

        return null;
    }

    private static string PastTheQuota(PluginRefusal refusal) =>
        $"the render does not fit the plugin's disk quota. {refusal.Fix}";

    public Task<PluginStemSplitResult> SplitStemsAsync(
        string trackId,
        PluginStemCoverage coverage,
        PluginStemSet stemSet,
        CancellationToken ct = default
    ) =>
        PluginCallGuard.RunAsync(
            pluginId.ToString(),
            nameof(SplitStemsAsync),
            () => SplitStemsCoreAsync(trackId, coverage, stemSet, ct),
            PluginStemSplitResult.Refused,
            _logger
        );

    private async Task<PluginStemSplitResult> SplitStemsCoreAsync(
        string trackId,
        PluginStemCoverage coverage,
        PluginStemSet stemSet,
        CancellationToken ct
    )
    {
        // The 4stems model is not in the ffmpeg build yet. Saying so beats
        // silently handing back two stems a caller asked four of.
        if (stemSet == PluginStemSet.Four)
        {
            return PluginStemSplitResult.Refused("four-stem splitting is not available yet");
        }

        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            return PluginStemSplitResult.Refused(FfmpegMissing);
        }

        if (!StemsplitModelPresent())
        {
            return PluginStemSplitResult.Refused(StemsplitModelMissing);
        }

        if (!Guid.TryParse(trackId, out Guid id))
        {
            return PluginStemSplitResult.Refused($"track {trackId} has no file");
        }

        TrackFile? file = await LoadTrackFileAsync(id, ct);
        if (file is null)
        {
            return PluginStemSplitResult.Refused($"track {trackId} has no file");
        }

        // Only a windowed split needs the duration. A full split of a track
        // the scan never timed is still a perfectly good split, so it is not
        // refused for a value it does not use.
        StemWindow? window = BuildWindow(coverage, file.DurationSeconds);
        if (window is null)
        {
            return PluginStemSplitResult.Refused($"track {trackId} has no duration");
        }

        if (!await _oneRunAtATime.WaitAsync(0, ct))
        {
            return PluginStemSplitResult.Refused(RunInProgress);
        }

        try
        {
            return await SplitInsideTheLockAsync(ffmpegPath, id, file, coverage, window, ct);
        }
        finally
        {
            _oneRunAtATime.Release();
        }
    }

    /// <summary>
    /// The split itself, with the one-ffmpeg-per-plugin lock already held.
    /// Split out so the lock's <c>finally</c> stays next to its
    /// <c>WaitAsync</c> instead of a hundred lines away from it.
    /// </summary>
    private async Task<PluginStemSplitResult> SplitInsideTheLockAsync(
        string ffmpegPath,
        Guid trackId,
        TrackFile file,
        PluginStemCoverage coverage,
        StemWindow window,
        CancellationToken ct
    )
    {
        await derivedStorage.CreateDirectoryAsync(TempFolder, ct);

        // ffmpeg writes the two stems as plain files first; only content that
        // finished being written gets a content-addressed key.
        Ulid batch = Ulid.NewUlid();
        string vocalsTemp = $"{TempFolder}/{batch}-vocals.opus";
        string accompanimentTemp = $"{TempFolder}/{batch}-accompaniment.opus";

        try
        {
            await using LocalPathLease inputLease = await storage.AcquireLocalPathAsync(
                file.Path,
                ct
            );

            // A lease on a path ffmpeg WRITES to, which LocalPathLease's
            // read-only staging contract only allows because the derived store
            // is always local. A remote one would need a put-from-lease helper.
            await using LocalPathLease vocalsLease = await derivedStorage.AcquireLocalPathAsync(
                vocalsTemp,
                ct
            );
            await using LocalPathLease accompanimentLease =
                await derivedStorage.AcquireLocalPathAsync(accompanimentTemp, ct);

            string[] arguments = PluginAudioArguments.StemSplit(
                inputLease.Path,
                window.Arguments,
                vocalsLease.Path,
                accompanimentLease.Path
            );

            string? bannerLine = null;
            Queue<string> stdErrTail = new();

            void CaptureStdErr(string line)
            {
                bannerLine ??= line;
                stdErrTail.Enqueue(line);
                if (stdErrTail.Count > 5)
                {
                    stdErrTail.Dequeue();
                }
            }

            ProcessResult? result = await RunFfmpegAsync(
                ffmpegPath,
                arguments,
                null,
                CaptureStdErr,
                ct,
                watchForExitMarker: true
            );

            if (result is null)
            {
                return PluginStemSplitResult.Refused(
                    $"stemsplit timed out after {RunTimeout.TotalSeconds:0} seconds"
                );
            }

            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "plugin {PluginId}: stemsplit exited with {Exit} for track {TrackId}: {StdErr}",
                    pluginId,
                    result.ExitCode,
                    trackId,
                    string.Join(" | ", stdErrTail)
                );

                return PluginStemSplitResult.Refused($"stemsplit exited with {result.ExitCode}");
            }

            // Exit code 0 is ffmpeg's word, not proof: a filter that produced
            // no frames still exits clean, and reading a file that is not
            // there would surface as an IOException from inside the store
            // rather than as something a plugin can act on.
            if (
                !await derivedStorage.ExistsAsync(vocalsTemp, ct)
                || !await derivedStorage.ExistsAsync(accompanimentTemp, ct)
            )
            {
                _logger.LogWarning(
                    "plugin {PluginId}: stemsplit exited 0 but wrote no output for track {TrackId}",
                    pluginId,
                    trackId
                );

                return PluginStemSplitResult.Refused(NoStemOutput);
            }

            // Both files land in the store before anything is registered, so a
            // registration that refuses halfway leaves no stem stored without
            // its twin.
            DerivedAudioEntry vocals = await StoreTempAsync(vocalsTemp, ct);
            DerivedAudioEntry accompaniment = await StoreTempAsync(accompanimentTemp, ct);

            string producerVersion =
                $"{AppFiles.StemsplitModel}@{PluginAudioArguments.FfmpegVersion(bannerLine)}";

            IPluginMusicAnalysisWriter writer = writerFactory.CreateFor(pluginId);

            (string Kind, DerivedAudioEntry Entry)[] produced =
            [
                ("vocals", vocals),
                ("accompaniment", accompaniment),
            ];

            // Registered as one write: a vocals row whose accompaniment was
            // refused describes a split no renderer can use.
            PluginWriteResult write = await writer.RegisterStemsAsync(
                produced
                    .Select(stem => new PluginTrackStem(
                        trackId,
                        stem.Kind,
                        coverage,
                        window.StartMs,
                        window.EndMs,
                        StemFormats.Opus,
                        OpusSampleRate,
                        stem.Entry.Key,
                        producerVersion
                    ))
                    .ToList(),
                ct
            );

            if (!write.Ok)
            {
                return PluginStemSplitResult.Refused(write.Refusal!);
            }

            List<PluginStemFile> stems = produced
                .Select(stem => new PluginStemFile(
                    stem.Kind,
                    coverage,
                    stem.Entry.Key,
                    stem.Entry.Bytes
                ))
                .ToList();

            return new PluginStemSplitResult(stems, null);
        }
        finally
        {
            // Every way out of the block above would otherwise strand two
            // scratch files. CancellationToken.None deliberately: cleanup after
            // a cancellation is exactly when it matters most.
            await DeleteTempAsync(vocalsTemp, CancellationToken.None);
            await DeleteTempAsync(accompanimentTemp, CancellationToken.None);
        }
    }

    /// <summary>
    /// Copies one finished temp file into the content-addressed store. The
    /// temp itself is dropped by the caller's <c>finally</c>, so a throw in
    /// here cannot strand it.
    /// </summary>
    private async Task<DerivedAudioEntry> StoreTempAsync(string tempPath, CancellationToken ct)
    {
        DerivedAudioEntry entry;
        await using (Stream content = await derivedStorage.OpenReadAsync(tempPath, ct))
        {
            entry = await store.PutAsync(content, StemFormats.OggContentType, ct);
        }

        return entry;
    }

    /// <summary>
    /// Best-effort: a temp file that will not go is a wasted megabyte the
    /// derived store's own sweep clears later, never a reason to fail a split
    /// whose stems are already safely stored.
    /// </summary>
    private async Task DeleteTempAsync(string tempPath, CancellationToken ct)
    {
        try
        {
            if (await derivedStorage.ExistsAsync(tempPath, ct))
            {
                await derivedStorage.DeleteAsync(tempPath, ct);
            }
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                exception,
                "plugin {PluginId}: could not remove the stem scratch file",
                pluginId
            );
        }
    }

    /// <summary>
    /// One ffmpeg run with the host's own timeout on top of the caller's
    /// token. Null means the host ended it - the timeout fired, or
    /// <paramref name="endRun" /> did - and the caller decides what that is
    /// worth to it.
    /// <para>
    /// The Windows ffmpeg build links ggml (stemsplit, whisper) against GCC's
    /// libgomp. At process exit libgomp tears down its worker pool, and on
    /// this MinGW build that teardown intermittently deadlocks - reproduced
    /// 2026-09-16 on the owner's server, roughly two splits in three, the
    /// minidump showing the exit handler still waiting on its own worker
    /// pool. ffmpeg's outputs are already complete - trailer and AVIO
    /// statistics included - by the time it writes "progress=end" to stdout
    /// (stemsplit asks for that marker; see <see cref="PluginAudioArguments.StemSplit" />),
    /// so <paramref name="watchForExitMarker" /> starts the kill signal's own
    /// grace timer on that line - the same mechanism
    /// <see cref="NoMercy.Encoder.Execution.FfmpegExecutor" /> uses for the same reason - and
    /// a process still running once it fires is stuck at exit, not mid-write;
    /// ending it loses nothing. The root fix is GGML_OPENMP=OFF in
    /// nomercy-ffmpeg; this is the guard for every installation until that
    /// ships.
    /// </para>
    /// </summary>
    /// <param name="watchForExitMarker">
    /// Stem split only. A plugin's own filter-graph text can print anything,
    /// "progress=end" included, on purpose or by coincidence - the marker
    /// watch would end a run the plugin never asked to end, so
    /// <see cref="RunFilterGraphCoreAsync" /> never sets this. A render does:
    /// it asks for the same progress report the stem split does.
    /// </param>
    /// <param name="endRun">
    /// A reason of the caller's own to end the run early, such as a render
    /// that stopped making progress. Ends it at once, through the runner's
    /// kill signal, and the run comes back as null whatever the process had
    /// to say on its way out.
    /// </param>
    private async Task<ProcessResult?> RunFfmpegAsync(
        string ffmpegPath,
        string[] arguments,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        CancellationToken ct,
        bool watchForExitMarker = false,
        TimeSpan? timeout = null,
        CancellationToken endRun = default
    )
    {
        TimeSpan runTimeout = timeout ?? RunTimeout;
        using CancellationTokenSource runCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            endRun
        );
        runCts.CancelAfter(runTimeout);

        // Disposed after the run has returned, as FfmpegExecutor does - never
        // through a `using`.
        //
        // endRun drives this kill signal as well as the cancellation above.
        // On the cancellation alone the runner gives ffmpeg its five-second
        // grace period first and, where that runs out, returns without
        // waiting for the process to be gone: five more seconds of writing
        // for a run that was ended for outgrowing a quota, and files the
        // caller cannot remove because ffmpeg still holds them open. The
        // kill signal ends the process at once, and the cancellation path
        // then only waits for a process that is already on its way out.
        CancellationTokenSource graceCts = new(Timeout.InfiniteTimeSpan, Clock);
        CancellationTokenSource killCts = CancellationTokenSource.CreateLinkedTokenSource(
            endRun,
            graceCts.Token
        );
        bool finishedItsOutputs = false;

        // Read after the run, where both mean the same thing: the grace
        // timer is what ended it, not the caller.
        bool EndedAtExit() =>
            finishedItsOutputs
            && killCts.IsCancellationRequested
            && !endRun.IsCancellationRequested
            && !ct.IsCancellationRequested;

        void ObserveStdOut(string line)
        {
            onStdOut?.Invoke(line);

            if (watchForExitMarker && line == "progress=end")
            {
                _logger.LogDebug(
                    "plugin {PluginId}: progress=end received, ending the run after {Grace}s if it has not exited",
                    pluginId,
                    ExitGracePeriod.TotalSeconds
                );
                finishedItsOutputs = true;
                graceCts.CancelAfter(ExitGracePeriod);
            }
        }

        try
        {
            ProcessResult result = await processRunner.RunAsync(
                ffmpegPath,
                arguments,
                ObserveStdOut,
                onStdErr,
                AppFiles.FfmpegFolder,
                runCts.Token,
                killCts.Token
            );

            if (!endRun.IsCancellationRequested)
            {
                return result;
            }

            // The runner reports a process its kill signal ended as exit 0.
            // For the exit hang that is the truth; for a run the caller
            // ended it is not, and when what ended it was the caller's own
            // cancellation, that is what the caller gets back.
            ct.ThrowIfCancellationRequested();
            return null;
        }
        catch (OperationCanceledException) when (EndedAtExit())
        {
            // A runner that surfaces the grace-period kill as a thrown
            // cancellation instead of returning normally - the way the real
            // ProcessRunner does for its own kill signal, ExitCode 0 - still
            // finished successfully: ffmpeg's own "progress=end" already said
            // the outputs were complete. The caller's own token is checked
            // too, so a cancellation the caller itself asked for is never
            // reported back as success.
            return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The caller that passed endRun knows why it ended the run and
            // says so itself; calling that a timeout would send the owner
            // looking for the wrong thing.
            if (!endRun.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "plugin {PluginId}: ffmpeg run timed out after {Seconds}s",
                    pluginId,
                    runTimeout.TotalSeconds
                );
            }

            return null;
        }
        finally
        {
            // Whether the workaround actually fired - the marker-received log
            // above only says the grace timer was started, not that ffmpeg
            // was still stuck once it elapsed. The owner reads this line to
            // judge the ffmpeg root fix (GGML_OPENMP=OFF): once that ships,
            // it should stop appearing.
            if (EndedAtExit())
            {
                _logger.LogDebug(
                    "plugin {PluginId}: ffmpeg finished its outputs but did not exit; ended it after {Grace}s",
                    pluginId,
                    ExitGracePeriod.TotalSeconds
                );
            }

            killCts.Dispose();
            graceCts.Dispose();
        }
    }

    /// <summary>
    /// The configured ffmpeg, or null when there is none. Read from the
    /// override rather than from <see cref="EncoderOptions.FfmpegPath" />,
    /// which throws when nothing was configured - an unconfigured server is a
    /// refusal here, not a crash inside a plugin's sweep.
    /// </summary>
    private string? ResolveFfmpegPath()
    {
        string? path = options.FfmpegPathOverride;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return storage.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            // The capability probe treats an unreadable path the same way:
            // whatever the reason, this server cannot run ffmpeg from here.
            return null;
        }
    }

    /// <summary>
    /// Whether the Spleeter GGUF is on this machine. The capability probe is
    /// the authority, but it runs deferred, so before its first report the
    /// same file check it makes stands in - better than refusing every split
    /// during the minutes after a restart.
    /// <para>
    /// The fallback reads <see cref="EncoderOptions.StemsplitModelPath" />
    /// first, which is the path the probe itself reads, so on a server that
    /// configured an override both answer about the same file. Only when that
    /// option is blank does this fall back to
    /// <see cref="AppFiles.StemsplitModelPath" /> - the probe never does, but
    /// it also never runs with a blank option, since the host sets the option
    /// from exactly that constant.
    /// </para>
    /// </summary>
    private bool StemsplitModelPresent() =>
        ModelPresent(
            report => report.StemsplitModelPresent,
            options.StemsplitModelPath,
            AppFiles.StemsplitModelPath
        );

    private bool WhisperModelPresent() =>
        ModelPresent(
            report => report.WhisperModelPresent,
            options.WhisperModelPath,
            AppFiles.WhisperModelPath
        );

    private bool VadModelPresent() =>
        ModelPresent(report => report.VadModelPresent, options.VadModelPath, AppFiles.VadModelPath);

    /// <summary>
    /// The refusal for the first model a graph needs and this machine lacks,
    /// or null. Each filter exists in the build whether or not its model was
    /// ever downloaded, so ffmpeg would start, read the whole input and only
    /// then fail. Cheaper, and far clearer, to say so up front.
    /// </summary>
    /// <param name="everyStemsplitNeedsTheServersModel">
    /// True for a filter-graph run, which asks for the server's model the
    /// moment its text says "stemsplit", as it always has. False for a
    /// render, which asks only when the graph names that model: a render may
    /// run stemsplit on a model file of the plugin's own, or on one of the
    /// filter's own presets, and neither needs anything the server installs.
    /// </param>
    private string? MissingModel(string graphText, bool everyStemsplitNeedsTheServersModel)
    {
        bool needsStemsModel = everyStemsplitNeedsTheServersModel
            ? graphText.Contains("stemsplit", StringComparison.Ordinal)
            : PluginGraphTokens.UsesStemsModel(graphText);

        if (needsStemsModel && !StemsplitModelPresent())
        {
            return StemsplitModelMissing;
        }

        if (PluginGraphTokens.UsesWhisperModel(graphText) && !WhisperModelPresent())
        {
            return WhisperModelMissing;
        }

        if (PluginGraphTokens.UsesVadModel(graphText) && !VadModelPresent())
        {
            return VadModelMissing;
        }

        return PluginGraphTokens.UsesAlignModel(graphText) ? MissingForLyricsAlign() : null;
    }

    /// <summary>
    /// What a graph that names <c>{alignModel}</c> still lacks on this
    /// server, or null: the filter, the model, or the phoneme tables the
    /// filter reads next to ffmpeg.
    /// <para>
    /// The model is the one file here that is not fetched at setup. It is
    /// 600 MB and only a plugin that aligns lyrics uses it, so the first
    /// graph that names it is what asks for it: that graph is refused, the
    /// download starts, and a later one runs.
    /// </para>
    /// </summary>
    private string? MissingForLyricsAlign()
    {
        // Only a probe can say the filter is not there. Before the first
        // report the graph is let through, and an ffmpeg without the filter
        // says so itself.
        if (capabilityProbe.GetCachedReport() is { LyricsAlignFilterPresent: false })
        {
            return AlignFilterMissing;
        }

        // Asked of the disk and not of the report: the probe runs once, at
        // start, and this file arrives long after it.
        if (!FilePresent(options.LyricsAlignModelPath, AppFiles.LyricsAlignModelPath))
        {
            if (alignModelInstaller is null)
            {
                return AlignModelMissing;
            }

            alignModelInstaller.RequestInstall();
            return AlignModelOnItsWay;
        }

        return FilePresent(options.EspeakDataMarkerPath, AppFiles.EspeakDataMarkerPath)
            ? null
            : EspeakDataMissing;
    }

    private bool ModelPresent(
        Func<CapabilityReport, bool> fromReport,
        string? configuredPath,
        string fallbackPath
    )
    {
        CapabilityReport? report = capabilityProbe.GetCachedReport();
        if (report is not null)
        {
            return fromReport(report);
        }

        return FilePresent(configuredPath, fallbackPath);
    }

    /// <summary>
    /// Whether a file the server provisions is on this machine: at the
    /// configured path, or at <paramref name="fallbackPath" /> when nothing
    /// was configured. An unreadable path is an absent file.
    /// </summary>
    private bool FilePresent(string? configuredPath, string fallbackPath)
    {
        string path = string.IsNullOrWhiteSpace(configuredPath) ? fallbackPath : configuredPath;

        try
        {
            return storage.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Turns the plugin's input into a local path ffmpeg can open, or a reason it cannot.</summary>
    private async Task<InputResolution> ResolveInputAsync(
        PluginAudioInput input,
        CancellationToken ct
    )
    {
        if (input.PluginFilePath is not null)
        {
            return ResolvePluginFileInput(input);
        }

        if (input.StorageKey is not null)
        {
            // Checked before the store is asked anything: answering about a key
            // means slicing it into a path, and this key came from a plugin.
            if (!DerivedAudioKey.IsValid(input.StorageKey))
            {
                return new(null, $"storage key {input.StorageKey} is not in the derived store");
            }

            // One call does both jobs. A run has ten minutes to finish and
            // eviction only spares what was used inside its grace window, so
            // the key is kept alive before ffmpeg opens the file rather than
            // after it closes it - and the touch answers whether there was
            // still an entry to keep alive, which is the same question a
            // separate existence check used to ask a moment earlier.
            //
            // A true is enough for the whole run: the touch moved LastUsedAt
            // inside the grace window under the store's own key lock, so
            // DeleteIfStillColdAsync's re-check under that same lock finds the
            // key warm and skips it for the rest of the sweep.
            if (!await store.TouchAsync(input.StorageKey, ct))
            {
                return new(null, $"storage key {input.StorageKey} is not in the derived store");
            }

            return new(
                await derivedStorage.AcquireLocalPathAsync(
                    store.RelativePath(input.StorageKey),
                    ct
                ),
                null
            );
        }

        if (input.TrackId is null)
        {
            return new(null, "the input names neither a track nor a derived file");
        }

        if (!Guid.TryParse(input.TrackId, out Guid trackId))
        {
            return new(null, $"track {input.TrackId} has no file");
        }

        TrackFile? file = await LoadTrackFileAsync(trackId, ct);
        if (file is null)
        {
            return new(null, $"track {input.TrackId} has no file");
        }

        return new(await storage.AcquireLocalPathAsync(file.Path, ct), null);
    }

    /// <summary>
    /// A file in the plugin's own data folder as an input. Through the same
    /// checks a <c>{pluginFile:…}</c> token gets, and refused in the same
    /// words.
    /// </summary>
    private InputResolution ResolvePluginFileInput(PluginAudioInput input)
    {
        if (input.TrackId is not null || input.StorageKey is not null)
        {
            return new(null, "the input names more than one source");
        }

        ResolvedPluginPath path = PluginGraphTokens.ResolvePluginPath(
            input.PluginFilePath,
            dataFolderPath,
            "a plugin file input"
        );
        if (path.Refusal is not null)
        {
            return new(null, path.Refusal);
        }

        if (PluginGraphTokens.PassesThroughALink(dataFolderPath!, path.FullPath, storageDriver))
        {
            return new(
                null,
                $"plugin file '{input.PluginFilePath}' leaves the plugin's data folder"
            );
        }

        if (!storageDriver.FileExists(path.FullPath))
        {
            return new(null, $"plugin file '{input.PluginFilePath}' does not exist");
        }

        // Already a local file, and the plugin's own: nothing to stage and
        // nothing to clean up, so the lease only carries the path.
        return new(new LocalPathLease(path.FullPath), null);
    }

    /// <summary>
    /// A track's file and duration, or null when the library has no file for
    /// it - an unknown id and a track the scan never found a file for are the
    /// same absence to a caller that wanted to run ffmpeg over it.
    /// </summary>
    private async Task<TrackFile?> LoadTrackFileAsync(Guid trackId, CancellationToken ct)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        Track? track = await context
            .Tracks.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == trackId, ct);

        if (
            track is null
            || string.IsNullOrWhiteSpace(track.HostFolder)
            || string.IsNullOrWhiteSpace(track.Filename)
        )
        {
            return null;
        }

        return new(
            storageDriver.CombinePath(track.HostFolder, track.Filename),
            PluginMusicQuery.ParseDurationSeconds(track.Duration)
        );
    }

    /// <summary>
    /// The seek arguments for a coverage, and the same window in milliseconds
    /// for the register row. Null when a window was asked for and the track's
    /// duration is unknown, which is the one case that cannot be computed.
    /// </summary>
    private static StemWindow? BuildWindow(PluginStemCoverage coverage, double? durationSeconds)
    {
        if (coverage == PluginStemCoverage.Full)
        {
            return new([], null, null);
        }

        if (durationSeconds is not > 0)
        {
            return null;
        }

        double duration = durationSeconds.Value;

        if (coverage == PluginStemCoverage.MixIn)
        {
            double end = duration * MixInShare;
            return new(["-t", PluginAudioArguments.Seconds(end)], 0, Milliseconds(end));
        }

        double start = duration * MixOutStart;
        return new(
            ["-ss", PluginAudioArguments.Seconds(start)],
            Milliseconds(start),
            Milliseconds(duration)
        );
    }

    private static int Milliseconds(double seconds) => (int)Math.Round(seconds * 1000);

    /// <summary>A resolved input, or the reason there is none. Exactly one of the two is set.</summary>
    private sealed record InputResolution(LocalPathLease? Lease, string? Refusal);

    /// <summary>Where a track's audio is, and how long it runs when the library knows.</summary>
    private sealed record TrackFile(string Path, double? DurationSeconds);

    /// <summary>What a coverage means to ffmpeg, and what it means to the stem register.</summary>
    private sealed record StemWindow(string[] Arguments, int? StartMs, int? EndMs);
}
