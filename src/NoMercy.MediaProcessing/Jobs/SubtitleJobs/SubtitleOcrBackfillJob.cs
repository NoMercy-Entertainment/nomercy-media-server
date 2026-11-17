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

using Microsoft.Extensions.DependencyInjection;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Subtitles;
using NoMercy.MediaProcessing.Files;
using NoMercy.Storage;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using Serilog.Events;
using Logger = NoMercy.NmSystem.SystemCalls.Logger;

namespace NoMercy.MediaProcessing.Jobs.SubtitleJobs;

/// <summary>
/// OCRs every preserved bitmap-subtitle sidecar (<c>.sup</c>) of one title
/// folder into its <c>.vtt</c> sibling, in one queue job. A library scan enqueues this whenever it finds a bitmap
/// track with no usable text sibling — titles encoded before the OCR pipeline
/// worked never got their sidecar, and OCR is far too slow to run inline on the
/// scan thread. The job reads the <c>.sup</c> directly, so the original release
/// source is not required (it is usually long gone by the time a title is being
/// rescanned). Idempotent: it no-ops when the sidecar already exists or the
/// <c>.sup</c> is gone, so re-enqueuing across scans is harmless.
/// </summary>
[Serializable]
public class SubtitleOcrBackfillJob : IShouldQueue, IJobStorageInjector
{
    // Shares the encoder queue (the OCR ffmpeg run is encoder work), but at a
    // lower priority than a real encode (4) so live encodes always drain first.
    public string QueueName => "encoder";
    public int Priority => 2;

    public string FolderId { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string HostFolder { get; set; } = string.Empty;

    /// <summary>Every orphaned bitmap subtitle of the title folder, OCR'd in one job.</summary>
    public List<OrphanedBitmapSubtitle> Tracks { get; set; } = [];

    // Single-file fields of rows queued before Tracks existed; read only by PendingTracks.
    public string SupFileName { get; set; } = string.Empty;
    public string MediaTitle { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;

    private ISubtitleOcrEngine? _ocrEngine;
    private IStorageFactory? _storageFactory;

    public SubtitleOcrBackfillJob() { }

    public SubtitleOcrBackfillJob(
        string folderId,
        string driverId,
        string hostFolder,
        IEnumerable<OrphanedBitmapSubtitle> tracks
    )
    {
        FolderId = folderId;
        DriverId = driverId;
        HostFolder = hostFolder;
        Tracks = [.. tracks];
    }

    public IReadOnlyList<OrphanedBitmapSubtitle> PendingTracks() =>
        Tracks.Count > 0 ? Tracks
        : SupFileName.Length > 0 ? [new(SupFileName, MediaTitle, Language, Variant)]
        : [];

    public void InjectStorageServices(IServiceProvider serviceProvider)
    {
        _ocrEngine = serviceProvider.GetService<ISubtitleOcrEngine>();
        _storageFactory = serviceProvider.GetRequiredService<IStorageFactory>();
    }

    public async Task Handle()
    {
        if (_ocrEngine is null || _storageFactory is null)
            return;
        if (!Ulid.TryParse(FolderId, out Ulid folderId))
            return;
        if (!Ulid.TryParse(DriverId, out Ulid driverId))
            return;

        IStorage storage = _storageFactory.For(folderId, driverId, string.Empty);
        string subtitleFolder = storage.CombinePath(HostFolder, "subtitles");
        List<Exception> failures = [];

        foreach (OrphanedBitmapSubtitle orphan in PendingTracks())
        {
            try
            {
                await OcrOneAsync(_ocrEngine, storage, subtitleFolder, orphan);
            }
            catch (Exception ex)
            {
                // One unreadable track must not cost the others their OCR. The job
                // still fails afterwards, so the queue retries; done tracks then skip.
                Logger.App(
                    $"[SubtitleOcrBackfill] {orphan.SupName}: {ex.Message}",
                    LogEventLevel.Warning
                );
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException(failures);
    }

    private async Task OcrOneAsync(
        ISubtitleOcrEngine ocrEngine,
        IStorage storage,
        string subtitleFolder,
        OrphanedBitmapSubtitle orphan
    )
    {
        string vttPath = storage.CombinePath(
            subtitleFolder,
            $"{orphan.MediaTitle}.{orphan.Language}.{orphan.Variant}.vtt"
        );

        // A prior pass already produced the sidecar — nothing to do.
        if (storage.Exists(vttPath))
            return;

        string supPath = storage.CombinePath(subtitleFolder, orphan.SupName);
        if (!storage.Exists(supPath))
            return;

        // A standalone .sup carries exactly one PGS stream, so the stream index is
        // 0. The target keeps the .sup's {MediaTitle}.{lang}.{variant} stem so the
        // scan pairs the new .vtt with the bitmap track.
        SubtitleTrack track = await ocrEngine.OcrAsync(
            supPath,
            0,
            orphan.Language,
            SubtitleCodecType.WebVtt,
            CancellationToken.None,
            storage,
            new OcrSidecarTarget(storage, HostFolder, orphan.MediaTitle, orphan.Variant)
        );

        Logger.App(
            $"[SubtitleOcrBackfill] {orphan.MediaTitle}.{orphan.Language}.{orphan.Variant}: OCR'd {track.CueCount} cues from {orphan.SupName}",
            LogEventLevel.Information
        );
    }
}
