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

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Startup;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.NmSystem.Information;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
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
    ILogger<PluginAudioTools>? logger = null
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

    /// <summary>The derived store's own scratch folder; its eviction sweep also cleans it.</summary>
    private const string TempFolder = "tmp";

    private const string OpusContentType = "audio/ogg";
    private const string OpusFormat = "opus";
    private const int OpusSampleRate = 48000;

    /// <summary>The share of a track a mix into it draws from.</summary>
    private const double MixInShare = 0.20;

    /// <summary>Where the window a mix out of a track draws from starts.</summary>
    private const double MixOutStart = 0.75;

    private const string FfmpegMissing = "ffmpeg is not installed";
    private const string StemsplitModelMissing = "the stemsplit model is not installed";
    private const string RunInProgress = "another ffmpeg run of this plugin is still in progress";
    private const string NoStemOutput = "stemsplit produced no output";

    private readonly SemaphoreSlim _oneRunAtATime = new(1, 1);

    private readonly ILogger<PluginAudioTools> _logger =
        logger ?? NullLogger<PluginAudioTools>.Instance;

    public async Task<PluginAudioRunResult> RunFilterGraphAsync(
        PluginAudioInput input,
        PluginFilterGraph graph,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        CancellationToken ct = default
    )
    {
        try
        {
            return await RunFilterGraphCoreAsync(input, graph, onStdOut, onStdErr, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PluginAudioRunResult.Refused(Unexpected(exception, nameof(RunFilterGraphAsync)));
        }
    }

    private async Task<PluginAudioRunResult> RunFilterGraphCoreAsync(
        PluginAudioInput input,
        PluginFilterGraph graph,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        CancellationToken ct
    )
    {
        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            return PluginAudioRunResult.Refused(FfmpegMissing);
        }

        string text = PluginAudioArguments.ApplyModelToken(graph.Text);

        // The filter exists in the build whether or not the model was ever
        // downloaded, so ffmpeg would start, read the whole input and only
        // then fail. Cheaper, and far clearer, to say so up front.
        if (text.Contains("stemsplit", StringComparison.Ordinal) && !StemsplitModelPresent())
        {
            return PluginAudioRunResult.Refused(StemsplitModelMissing);
        }

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
                ct
            );

            // Null is the timeout: -2 rather than a refusal, because the run
            // did start - the plugin's graph was accepted and its callbacks
            // saw whatever ffmpeg managed to print before the clock ran out.
            return result is null
                ? new PluginAudioRunResult(-2, null)
                : new PluginAudioRunResult(result.ExitCode, null);
        }
        finally
        {
            _oneRunAtATime.Release();
        }
    }

    public async Task<PluginStemSplitResult> SplitStemsAsync(
        string trackId,
        PluginStemCoverage coverage,
        PluginStemSet stemSet,
        CancellationToken ct = default
    )
    {
        try
        {
            return await SplitStemsCoreAsync(trackId, coverage, stemSet, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PluginStemSplitResult.Refused(Unexpected(exception, nameof(SplitStemsAsync)));
        }
    }

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

            // A lease on a path ffmpeg WRITES to. LocalPathLease documents a
            // read-only staging contract - a future remote driver would stage
            // the object into a temp file and drop it on dispose, which is the
            // wrong direction for an output. It holds here because the derived
            // store is always local storage (a LocalStorage scoped to
            // AppFiles.DerivedAudioPath, see ServiceConfiguration.Core), so the
            // lease hands back the real path unchanged. The day the derived
            // store can live on a remote driver, this needs a
            // put-from-lease helper on IStorage rather than a plain lease.
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
                ct
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
                        OpusFormat,
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
            // Every way out of the block above - a refusal, a cancelled token,
            // a throw from the store - would otherwise leave two scratch files
            // for the derived store's eviction sweep to find hours later.
            // CancellationToken.None deliberately: cleanup after a cancellation
            // is exactly when it matters, and deleting a file the caller no
            // longer wants is not work that should itself be cancellable.
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
            entry = await store.PutAsync(content, OpusContentType, ct);
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
    /// token. Null means the timeout fired; the caller decides what that is
    /// worth to it.
    /// </summary>
    private async Task<ProcessResult?> RunFfmpegAsync(
        string ffmpegPath,
        string[] arguments,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        CancellationToken ct
    )
    {
        using CancellationTokenSource runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(RunTimeout);

        try
        {
            return await processRunner.RunAsync(
                ffmpegPath,
                arguments,
                onStdOut,
                onStdErr,
                AppFiles.FfmpegFolder,
                runCts.Token
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "plugin {PluginId}: ffmpeg run timed out after {Seconds}s",
                pluginId,
                RunTimeout.TotalSeconds
            );
            return null;
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
    private bool StemsplitModelPresent()
    {
        CapabilityReport? report = capabilityProbe.GetCachedReport();
        if (report is not null)
        {
            return report.StemsplitModelPresent;
        }

        string? modelPath = options.StemsplitModelPath;
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            modelPath = AppFiles.StemsplitModelPath;
        }

        try
        {
            return storage.Exists(modelPath);
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
        if (input.StorageKey is not null)
        {
            // Checked before the store is asked anything: answering about a key
            // means slicing it into a path, and this key came from a plugin.
            if (
                !DerivedAudioKey.IsValid(input.StorageKey)
                || !await store.ExistsAsync(input.StorageKey, ct)
            )
            {
                return new(null, $"storage key {input.StorageKey} is not in the derived store");
            }

            // A run has ten minutes to finish and eviction only spares what was
            // used inside its grace window, so the key is kept alive before
            // ffmpeg opens the file rather than after it closes it.
            await store.TouchAsync(input.StorageKey, ct);

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
            return new(["-t", Seconds(end)], 0, Milliseconds(end));
        }

        double start = duration * MixOutStart;
        return new(["-ss", Seconds(start)], Milliseconds(start), Milliseconds(duration));
    }

    /// <summary>
    /// Turns a failure nobody planned for - a mount that went away, a locked
    /// database file - into a refusal the caller can read, and puts the real
    /// exception on the server's own record. A plugin sweeping a library
    /// unattended loses one track this way instead of the whole sweep.
    /// Cancellation the caller asked for never comes through here.
    /// </summary>
    private string Unexpected(Exception exception, string member)
    {
        _logger.LogWarning(
            exception,
            "plugin {PluginId}: {Member} failed inside the server",
            pluginId,
            member
        );

        return $"the server could not complete this call: {exception.GetType().Name}";
    }

    /// <summary>Seconds as ffmpeg reads them: invariant, and no decimal tail when there is none.</summary>
    private static string Seconds(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static int Milliseconds(double seconds) => (int)Math.Round(seconds * 1000);

    /// <summary>A resolved input, or the reason there is none. Exactly one of the two is set.</summary>
    private sealed record InputResolution(LocalPathLease? Lease, string? Refusal);

    /// <summary>Where a track's audio is, and how long it runs when the library knows.</summary>
    private sealed record TrackFile(string Path, double? DurationSeconds);

    /// <summary>What a coverage means to ffmpeg, and what it means to the stem register.</summary>
    private sealed record StemWindow(string[] Arguments, int? StartMs, int? EndMs);
}
