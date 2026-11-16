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
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The host's <see cref="IStemsplitSourceSelector" />: RoFormer wherever its
/// models are installed, unless the filter refused it on this machine as it
/// is now. The refusal is kept in a small JSON file beside the server's
/// other configuration, so it outlives a restart.
/// <para>
/// "This machine as it is now" is the ffmpeg binary (its size and time, which
/// every release the server installs changes) and the GPU list with each
/// driver version, hashed the way the encoder's driver-change detection does.
/// The GPU list is only read when there is a refusal to check, and kept for
/// <see cref="GpuListLifetime" />, so a library sweep does not ask the system
/// for its GPUs once per track.
/// </para>
/// </summary>
public sealed class StemsplitSourceSelector(
    IStorage storage,
    IHardwareDetector hardwareDetector,
    EncoderOptions options,
    ILogger<StemsplitSourceSelector> logger
) : IStemsplitSourceSelector
{
    /// <summary>How long one reading of the GPU list is trusted.</summary>
    internal static readonly TimeSpan GpuListLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a refusal for want of free GPU memory holds before the filter
    /// is asked again: a day. What held the memory may be gone by then.
    /// </summary>
    internal static readonly TimeSpan MemoryRefusalLifetime = TimeSpan.FromHours(24);

    // The hash of an empty GPU list, which is what a failed detection returns.
    private static readonly string NoGpus = new DriverFingerprint([]).ComputeHash();

    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _loaded;
    private StemsplitVerdict? _verdict;
    private string? _gpus;
    private DateTimeOffset _gpusReadAt;

    /// <summary>Where the verdict is kept. Init-only so a test can point it at a folder of its own.</summary>
    internal string VerdictPath { get; init; } = AppFiles.StemsplitVerdictPath;

    /// <summary>Where the RoFormer models are looked for: beside ffmpeg, its working directory.</summary>
    internal string ModelFolder { get; init; } = AppFiles.FfmpegFolder;

    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    public async Task<StemsplitSource> ChooseAsync(
        RoformerPreset preset,
        CancellationToken ct = default
    )
    {
        if (!ModelsInstalled(preset))
            return StemsplitSource.Spleeter;

        await _gate.WaitAsync(ct);
        try
        {
            StemsplitVerdict? verdict = LoadVerdict();
            if (verdict is null || !verdict.Refuses(preset))
                return StemsplitSource.Roformer(preset);

            if (verdict.HasExpired(Clock.GetUtcNow()))
            {
                logger.LogInformation(
                    "A day has passed since stemsplit refused RoFormer for want of free GPU memory; asking it again"
                );
                ForgetVerdict();
                return StemsplitSource.Roformer(preset);
            }

            string gpus = await GpusAsync(ct);
            if (verdict.Holds(FfmpegBuild(), gpus, gpusUnknown: gpus == NoGpus))
                return StemsplitSource.Spleeter;

            logger.LogInformation(
                "The ffmpeg build or the GPUs changed since stemsplit refused RoFormer on this machine; asking it again"
            );
            ForgetVerdict();
            return StemsplitSource.Roformer(preset);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RememberRefusalAsync(
        IReadOnlyCollection<RoformerPreset> presets,
        string refusalLine,
        CancellationToken ct = default
    )
    {
        await _gate.WaitAsync(ct);
        try
        {
            string ffmpegBuild = FfmpegBuild();
            string gpus = await GpusAsync(ct);

            // A refusal adds to one given on the same machine; one given on
            // another machine, or another build, no longer says anything.
            StemsplitVerdict? earlier = LoadVerdict();
            DateTimeOffset now = Clock.GetUtcNow();
            IEnumerable<string> kept =
                earlier is not null
                && !earlier.HasExpired(now)
                && earlier.Holds(ffmpegBuild, gpus, gpusUnknown: gpus == NoGpus)
                    ? earlier.RefusedPresets
                    : [];

            List<string> refused = [.. kept.Union(Refused(presets, refusalLine))];

            // Only the want of any GPU is for good; a want of memory is asked
            // again after a day.
            StemsplitVerdict verdict = new(
                ffmpegBuild,
                gpus,
                refused,
                refusalLine.Trim(),
                now,
                refused.Contains(StemsplitVerdict.EveryPreset) ? null : now + MemoryRefusalLifetime
            );
            SaveVerdict(verdict);

            logger.LogInformation(
                "stemsplit refused RoFormer on this machine ({Refusal}); stems are split with Spleeter until the ffmpeg build, the GPUs or a GPU driver change",
                refusalLine.Trim()
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The presets a refusal covers. For want of any GPU: all of them. For
    /// want of memory: the run's presets with that many models, or all of the
    /// run's presets when the count matches none of them.
    /// </summary>
    private static IEnumerable<string> Refused(
        IReadOnlyCollection<RoformerPreset> presets,
        string refusalLine
    )
    {
        if (StemsplitLog.RefusesEveryPreset(refusalLine))
            return [StemsplitVerdict.EveryPreset];

        int? models = StemsplitLog.RefusedRunModels(refusalLine);
        List<RoformerPreset> matching = presets
            .Where(preset => StemsplitSource.Models(preset).Count == models)
            .ToList();

        return (matching.Count > 0 ? matching : presets).Select(StemsplitSource.FilterName);
    }

    private bool ModelsInstalled(RoformerPreset preset) =>
        StemsplitSource
            .Models(preset)
            .All(model => Exists(Path.Combine(ModelFolder, model + ".gguf")));

    /// <summary>The ffmpeg binary's size and time; "none" without one.</summary>
    private string FfmpegBuild()
    {
        string? path = options.FfmpegPathOverride;
        if (string.IsNullOrWhiteSpace(path) || !Exists(path))
            return "none";

        try
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{storage.Size(path)}@{storage.LastModified(path).UtcDateTime:O}"
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "unreadable";
        }
    }

    private async Task<string> GpusAsync(CancellationToken ct)
    {
        DateTimeOffset now = Clock.GetUtcNow();
        if (_gpus is not null && now - _gpusReadAt < GpuListLifetime)
            return _gpus;

        IReadOnlyList<GpuDevice> gpus = await hardwareDetector.DetectGpusAsync(ct);
        _gpus = new DriverFingerprint(
            gpus.Select(
                    (gpu, index) =>
                        new GpuDriverInfo(
                            gpu.Vendor.ToString(),
                            gpu.Name,
                            gpu.DriverVersion ?? string.Empty,
                            index
                        )
                )
                .ToList()
        ).ComputeHash();
        _gpusReadAt = now;

        return _gpus;
    }

    /// <summary>
    /// The verdict on disk, read once. A file that cannot be read is no
    /// verdict: the worst that does is one more question to the filter.
    /// </summary>
    private StemsplitVerdict? LoadVerdict()
    {
        if (_loaded)
            return _verdict;

        _loaded = true;

        try
        {
            if (!storage.Exists(VerdictPath))
                return null;

            _verdict = JsonConvert.DeserializeObject<StemsplitVerdict>(
                Encoding.UTF8.GetString(storage.Read(VerdictPath))
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not read the stemsplit verdict at {Path}; asking the filter again",
                VerdictPath
            );
            _verdict = null;
        }

        return _verdict;
    }

    private void SaveVerdict(StemsplitVerdict verdict)
    {
        _verdict = verdict;
        _loaded = true;

        try
        {
            string? folder = Path.GetDirectoryName(VerdictPath);
            if (!string.IsNullOrWhiteSpace(folder))
                storage.CreateDirectory(folder);

            string staging = VerdictPath + ".tmp";
            storage.Write(
                staging,
                Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(verdict, Formatting.Indented))
            );
            if (storage.Exists(VerdictPath))
                storage.Delete(VerdictPath);
            storage.Move(staging, VerdictPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Kept in memory all the same: this process still knows, and the
            // next start asks the filter once more.
            logger.LogWarning(
                exception,
                "Could not save the stemsplit verdict to {Path}",
                VerdictPath
            );
        }
    }

    private void ForgetVerdict()
    {
        _verdict = null;
        _loaded = true;

        try
        {
            if (storage.Exists(VerdictPath))
                storage.Delete(VerdictPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not remove the stemsplit verdict at {Path}",
                VerdictPath
            );
        }
    }

    private bool Exists(string path)
    {
        try
        {
            return storage.Exists(path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }
}
