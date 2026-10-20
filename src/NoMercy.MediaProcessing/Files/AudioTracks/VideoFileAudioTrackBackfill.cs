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
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Analysis;
using NoMercy.Storage;
using Serilog.Events;
using Logger = NoMercy.NmSystem.SystemCalls.Logger;

namespace NoMercy.MediaProcessing.Files.AudioTracks;

/// <summary>
/// Stores one <see cref="VideoFileAudioTrack"/> per audio rendition of every video
/// file that has none: the HLS master lists the renditions and one segment of each
/// is probed. Files that already have rows are never touched.
/// </summary>
public sealed class VideoFileAudioTrackBackfill(
    IDbContextFactory<MediaContext> contextFactory,
    IStorageFactory storageFactory,
    IMediaAnalyzer mediaAnalyzer
)
{
    // Two sweeps running together would each insert rows for the same file.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    private sealed record PendingFile(Ulid Id, string Share, string HostFolder, string Filename);

    /// <returns>How many video files got audio track rows.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await RunLock.WaitAsync(ct);
        try
        {
            List<PendingFile> pending;
            await using (MediaContext context = await contextFactory.CreateDbContextAsync(ct))
            {
                pending = await context
                    .VideoFiles.AsNoTracking()
                    .Where(file =>
                        !context.VideoFileAudioTracks.Any(track => track.VideoFileId == file.Id)
                    )
                    .Select(file => new PendingFile(
                        file.Id,
                        file.Share,
                        file.HostFolder,
                        file.Filename
                    ))
                    .ToListAsync(ct);
            }

            int filled = 0;
            foreach (PendingFile file in pending)
            {
                try
                {
                    if (await FillAsync(file, ct))
                        filled++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.App(
                        $"[AudioTrackBackfill] {file.HostFolder}{file.Filename}: {ex.Message}",
                        LogEventLevel.Warning
                    );
                }
            }

            return filled;
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<bool> FillAsync(PendingFile file, CancellationToken ct)
    {
        if (!Ulid.TryParse(file.Share, out Ulid folderId))
            return false;

        Folder? folder;
        await using (MediaContext context = await contextFactory.CreateDbContextAsync(ct))
        {
            folder = await context
                .Folders.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == folderId, ct);
        }
        if (folder is null)
            return false;

        IStorage storage = storageFactory.For(folder.Id, folder.DriverId, string.Empty);
        string masterPath = storage.CombinePath(file.HostFolder, file.Filename.TrimStart('/'));
        if (!storage.Exists(masterPath))
            return false;

        string master = await storage.ReadAllTextAsync(masterPath, ct);

        List<VideoFileAudioTrack> tracks = [];
        foreach (HlsAudioRendition rendition in HlsAudioRenditionReader.Read(master))
        {
            VideoFileAudioTrack? track = await ProbeAsync(storage, file, rendition, ct);
            if (track is not null)
                tracks.Add(track);
        }

        if (tracks.Count == 0)
            return false;

        await using MediaContext writeContext = await contextFactory.CreateDbContextAsync(ct);
        writeContext.VideoFileAudioTracks.AddRange(tracks);
        await writeContext.SaveChangesAsync(ct);
        return true;
    }

    private async Task<VideoFileAudioTrack?> ProbeAsync(
        IStorage storage,
        PendingFile file,
        HlsAudioRendition rendition,
        CancellationToken ct
    )
    {
        string playlistPath = storage.CombinePath(file.HostFolder, rendition.Uri);
        if (!storage.Exists(playlistPath))
            return null;

        string playlist = await storage.ReadAllTextAsync(playlistPath, ct);
        string? probeTarget = FirstProbeTarget(playlist);
        if (probeTarget is null)
            return null;

        string probePath = storage.CombinePath(
            StoragePathHelpers.GetParent(playlistPath) ?? file.HostFolder,
            probeTarget
        );
        if (!storage.Exists(probePath))
            return null;

        MediaInfo info = await mediaAnalyzer.AnalyzeAsync(probePath, storage, ct);
        AudioStreamInfo? audio = info.AudioStreams.FirstOrDefault();
        if (audio is null)
            return null;

        return new()
        {
            VideoFileId = file.Id,
            Codec = audio.Codec,
            Profile = audio.Profile,
            Channels = audio.Channels,
            ChannelLayout = audio.ChannelLayout,
            SampleRate = audio.SampleRate,
            BitRateKbps = audio.BitRateKbps,
            Language = rendition.Language,
            IsDefault = rendition.IsDefault,
            GroupId = rendition.GroupId,
            Uri = rendition.Uri,
        };
    }

    // An fMP4 rendition cannot be probed from a bare segment; its init section can.
    private static string? FirstProbeTarget(string playlist)
    {
        string? segment = null;

        foreach (string rawLine in playlist.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                int start = line.IndexOf("URI=\"", StringComparison.Ordinal);
                if (start < 0)
                    continue;
                start += "URI=\"".Length;
                int end = line.IndexOf('"', start);
                if (end > start)
                    return line[start..end];
            }
            else if (segment is null && line.Length > 0 && line[0] != '#')
            {
                segment = line;
            }
        }

        return segment;
    }
}
