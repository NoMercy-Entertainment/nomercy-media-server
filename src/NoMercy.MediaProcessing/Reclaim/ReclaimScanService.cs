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
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.Reclaim;

public sealed class ReclaimScanService : IReclaimScanService
{
    private const string PartialStaleHoursKey = "reclaim.partial_stale_hours";
    private const int DefaultPartialStaleHours = 6;

    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly IStorageFactory _storageFactory;
    private readonly IConfigurationStore _configurationStore;
    private readonly ILogger<ReclaimScanService> _logger;
    private readonly Func<Ulid, Ulid, string, IReadOnlyList<FolderEntry>> _listFolderEntries;

    private readonly object _gate = new();
    private ReclaimScanState _state = ReclaimScanState.Idle;
    private DateTimeOffset? _lastScannedAt;
    private ReclaimScanResult? _latest;
    private int _scanning;

    public ReclaimScanService(
        IDbContextFactory<MediaContext> contextFactory,
        IStorageFactory storageFactory,
        IConfigurationStore configurationStore,
        ILogger<ReclaimScanService> logger
    )
        : this(contextFactory, storageFactory, configurationStore, logger, null) { }

    internal ReclaimScanService(
        IDbContextFactory<MediaContext> contextFactory,
        IStorageFactory storageFactory,
        IConfigurationStore configurationStore,
        ILogger<ReclaimScanService> logger,
        Func<Ulid, Ulid, string, IReadOnlyList<FolderEntry>>? listFolderEntriesOverride
    )
    {
        _contextFactory = contextFactory;
        _storageFactory = storageFactory;
        _configurationStore = configurationStore;
        _logger = logger;
        _listFolderEntries = listFolderEntriesOverride ?? ListFolderEntriesFromStorage;
    }

    public ReclaimScanState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
        private set
        {
            lock (_gate)
                _state = value;
        }
    }

    public DateTimeOffset? LastScannedAt
    {
        get
        {
            lock (_gate)
                return _lastScannedAt;
        }
        private set
        {
            lock (_gate)
                _lastScannedAt = value;
        }
    }

    public ReclaimScanResult? Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
        private set
        {
            lock (_gate)
                _latest = value;
        }
    }

    public Task StartScanAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
            return Task.CompletedTask;

        State = ReclaimScanState.Scanning;

        // The scan is detached from the caller's request lifetime: it must keep
        // running to completion even after the triggering HTTP request returns
        // and its token cancels, so the background chain always runs under
        // CancellationToken.None rather than the caller's ct.
        _ = Task.Run(() => RunScanAsync(CancellationToken.None), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task<long> DeleteItemAsync(string itemId, CancellationToken ct)
    {
        ReclaimableItem item = FindItemOrThrow(itemId);

        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);

        FreshFolderDbInfo? dbInfo = await ResolveFreshFolderDbContextAsync(
            context,
            item.Folder,
            ct
        );
        if (dbInfo is null)
            throw new InvalidOperationException(
                $"Folder '{item.Folder}' no longer resolves to a served copy; refusing to delete."
            );

        EnsureNoServedCopyConflict(item, dbInfo.Value.Rows);

        IStorage storage = _storageFactory.For(
            dbInfo.Value.FolderId,
            dbInfo.Value.DriverId,
            string.Empty
        );
        IReadOnlyList<StorageEntry> freshEntries = storage.List(
            item.Folder,
            null,
            recursive: false
        );

        ReclaimClassification fresh = ClassifyFreshFolderState(
            storage,
            freshEntries,
            dbInfo.Value.Rows
        );

        if (fresh.Kind != ReclaimKind.ReclaimableHls)
        {
            _logger.LogWarning(
                "[ReclaimScanService] Folder {Folder} is no longer reclaimable-HLS (now {Kind}) — refusing to delete item {ItemId}",
                item.Folder,
                fresh.Kind,
                item.Id
            );
            throw new InvalidOperationException(
                $"Folder '{item.Folder}' is no longer reclaimable — original missing or folder now protected."
            );
        }

        IReadOnlyList<string> confirmedTargets =
        [
            .. fresh.TargetNames.Select(name => StoragePathHelpers.Combine(item.Folder, name)),
        ];

        long freedBytes = DeleteTargets(storage, freshEntries, confirmedTargets);

        RemoveItemFromSnapshot(itemId);

        return freedBytes;
    }

    private ReclaimClassification ClassifyFreshFolderState(
        IStorage storage,
        IReadOnlyList<StorageEntry> freshEntries,
        IReadOnlyList<VideoFileScanRow> freshRows
    )
    {
        bool isProtected = freshRows.Any(row => IsServedPlaylist(row.Filename));

        List<FolderEntry> freshFolderEntries =
        [
            .. freshEntries.Select(entry => new FolderEntry(
                storage.GetName(entry.Path),
                entry.IsDirectory,
                entry.SizeBytes,
                entry.LastModified
            )),
        ];

        return ReclaimClassifier.Classify(
            freshFolderEntries,
            isProtected,
            DateTimeOffset.UtcNow,
            ResolvePartialStaleAfter()
        );
    }

    public async Task<(int count, long bytes)> SweepPartialsAsync(CancellationToken ct)
    {
        List<PartialJunkItem> partials;
        lock (_gate)
            partials = _latest?.PartialJunk.ToList() ?? [];

        if (partials.Count == 0)
            return (0, 0);

        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan partialStaleAfter = ResolvePartialStaleAfter();

        List<string> sweptFolders = [];
        long freedBytes = 0;

        foreach (PartialJunkItem partial in partials)
        {
            ct.ThrowIfCancellationRequested();

            long? swept = await SweepPartialFolderAsync(
                context,
                partial,
                now,
                partialStaleAfter,
                ct
            );
            if (swept is null)
                continue;

            freedBytes += swept.Value;
            sweptFolders.Add(partial.Folder);
        }

        if (sweptFolders.Count > 0)
            RemoveSweptPartialsFromSnapshot(sweptFolders);

        return (sweptFolders.Count, freedBytes);
    }

    private ReclaimableItem FindItemOrThrow(string itemId)
    {
        lock (_gate)
        {
            ReclaimableItem? found = _latest?.Items.FirstOrDefault(candidate =>
                candidate.Id == itemId
            );
            if (found is null)
                throw new KeyNotFoundException($"Reclaimable item '{itemId}' not found.");
            return found;
        }
    }

    private void EnsureNoServedCopyConflict(
        ReclaimableItem item,
        IReadOnlyList<VideoFileScanRow> freshRows
    )
    {
        foreach (VideoFileScanRow row in freshRows)
        {
            string servedPath = StoragePathHelpers.Combine(item.Folder, row.Filename);

            foreach (string targetPath in item.TargetPaths)
            {
                if (!ConflictsWithServedCopy(targetPath, servedPath))
                    continue;

                _logger.LogWarning(
                    "[ReclaimScanService] Refusing to delete {TargetPath} for item {ItemId} — it matches the currently served copy {ServedPath}",
                    targetPath,
                    item.Id,
                    servedPath
                );
                throw new InvalidOperationException(
                    $"Refusing to delete '{targetPath}' — it is the currently served copy '{servedPath}'."
                );
            }
        }
    }

    private static bool ConflictsWithServedCopy(string targetPath, string servedPath)
    {
        string normalizedTarget = targetPath.TrimEnd('/');
        string normalizedServed = servedPath.TrimEnd('/');

        if (string.Equals(normalizedTarget, normalizedServed, StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalizedServed.StartsWith(normalizedTarget + "/", StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedTarget.StartsWith(
            normalizedServed + "/",
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static long DeleteTargets(
        IStorage storage,
        IReadOnlyList<StorageEntry> freshEntries,
        IReadOnlyList<string> targetPaths
    )
    {
        long freedBytes = 0;
        foreach (string targetPath in targetPaths)
        {
            StorageEntry? entry = freshEntries.FirstOrDefault(candidate =>
                candidate.Path == targetPath
            );
            if (entry is null)
                continue;

            if (entry.IsDirectory)
                storage.DeleteDirectory(targetPath, recursive: true);
            else
                storage.Delete(targetPath);

            freedBytes += entry.SizeBytes;
        }

        return freedBytes;
    }

    private void RemoveItemFromSnapshot(string itemId)
    {
        lock (_gate)
        {
            if (_latest is null)
                return;

            List<ReclaimableItem> remaining =
            [
                .. _latest.Items.Where(candidate => candidate.Id != itemId),
            ];
            _latest = _latest with
            {
                Items = remaining,
                TotalReclaimableBytes = remaining.Sum(candidate => candidate.ReclaimableBytes),
            };
        }
    }

    private async Task<long?> SweepPartialFolderAsync(
        MediaContext context,
        PartialJunkItem partial,
        DateTimeOffset now,
        TimeSpan partialStaleAfter,
        CancellationToken ct
    )
    {
        FreshFolderDbInfo? dbInfo = await ResolveFreshFolderDbContextAsync(
            context,
            partial.Folder,
            ct
        );
        if (dbInfo is null)
            return null;

        IStorage storage;
        IReadOnlyList<StorageEntry> freshEntries;
        try
        {
            storage = _storageFactory.For(
                dbInfo.Value.FolderId,
                dbInfo.Value.DriverId,
                string.Empty
            );
            freshEntries = storage.List(partial.Folder, null, recursive: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ReclaimScanService] Could not re-list {Folder} during sweep — skipping",
                partial.Folder
            );
            return null;
        }

        bool isProtected = dbInfo.Value.Rows.Any(row => IsServedPlaylist(row.Filename));

        List<FolderEntry> freshFolderEntries =
        [
            .. freshEntries.Select(entry => new FolderEntry(
                storage.GetName(entry.Path),
                entry.IsDirectory,
                entry.SizeBytes,
                entry.LastModified
            )),
        ];

        ReclaimClassification classification = ReclaimClassifier.Classify(
            freshFolderEntries,
            isProtected,
            now,
            partialStaleAfter
        );

        if (classification.Kind != ReclaimKind.OrphanPartial)
        {
            _logger.LogInformation(
                "[ReclaimScanService] Folder {Folder} is no longer a stale masterless orphan — skipping sweep",
                partial.Folder
            );
            return null;
        }

        IReadOnlyList<string> confirmedTargets =
        [
            .. classification.TargetNames.Select(name =>
                StoragePathHelpers.Combine(partial.Folder, name)
            ),
        ];

        return DeleteTargets(storage, freshEntries, confirmedTargets);
    }

    private void RemoveSweptPartialsFromSnapshot(List<string> sweptFolders)
    {
        lock (_gate)
        {
            if (_latest is null)
                return;

            List<PartialJunkItem> remaining =
            [
                .. _latest.PartialJunk.Where(partial => !sweptFolders.Contains(partial.Folder)),
            ];

            _latest = _latest with
            {
                PartialJunk = remaining,
                TotalPartialJunkBytes = remaining.Sum(partial => partial.Bytes),
            };
        }
    }

    private async Task<FreshFolderDbInfo?> ResolveFreshFolderDbContextAsync(
        MediaContext context,
        string hostFolder,
        CancellationToken ct
    )
    {
        List<VideoFileScanRow> freshRows = await context
            .VideoFiles.AsNoTracking()
            .Where(videoFile => videoFile.HostFolder == hostFolder)
            .Select(videoFile => new VideoFileScanRow(
                videoFile.HostFolder,
                videoFile.Filename,
                videoFile.Share,
                videoFile.MovieId,
                videoFile.EpisodeId,
                null,
                null,
                null,
                null,
                null
            ))
            .ToListAsync(ct);

        if (freshRows.Count == 0)
        {
            _logger.LogWarning(
                "[ReclaimScanService] Folder {HostFolder} no longer has any VideoFile rows — refusing to act",
                hostFolder
            );
            return null;
        }

        if (!Ulid.TryParse(freshRows[0].Share, out Ulid folderId))
        {
            _logger.LogWarning(
                "[ReclaimScanService] VideoFile share for {HostFolder} is not a folder id — refusing to act",
                hostFolder
            );
            return null;
        }

        Dictionary<Ulid, Ulid> driverIdByFolderId = await ResolveDriverIdsAsync(
            context,
            freshRows,
            ct
        );
        if (!driverIdByFolderId.TryGetValue(folderId, out Ulid driverId))
        {
            _logger.LogWarning(
                "[ReclaimScanService] Folder {FolderId} for {HostFolder} not found — refusing to act",
                folderId,
                hostFolder
            );
            return null;
        }

        return new FreshFolderDbInfo(freshRows, folderId, driverId);
    }

    private async Task RunScanAsync(CancellationToken ct)
    {
        try
        {
            ReclaimScanResult result = await ScanAsync(ct);

            // One lock for all three: a caller that sees Completed must also see
            // the result and the time it was made, never Completed with no time.
            lock (_gate)
            {
                _latest = result;
                _lastScannedAt = DateTimeOffset.UtcNow;
                _state = ReclaimScanState.Completed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReclaimScanService] Scan failed");
            State = ReclaimScanState.Failed;
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private async Task<ReclaimScanResult> ScanAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan partialStaleAfter = ResolvePartialStaleAfter();

        await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);

        List<VideoFileScanRow> rows = await context
            .VideoFiles.AsNoTracking()
            .Select(videoFile => new VideoFileScanRow(
                videoFile.HostFolder,
                videoFile.Filename,
                videoFile.Share,
                videoFile.MovieId,
                videoFile.EpisodeId,
                videoFile.Movie != null ? videoFile.Movie.Title : null,
                videoFile.Episode != null ? videoFile.Episode.Tv.Title : null,
                videoFile.Episode != null ? videoFile.Episode.Tv.MediaType : null,
                videoFile.Episode != null ? (int?)videoFile.Episode.SeasonNumber : null,
                videoFile.Episode != null ? (int?)videoFile.Episode.EpisodeNumber : null
            ))
            .ToListAsync(ct);

        Dictionary<Ulid, Ulid> driverIdByFolderId = await ResolveDriverIdsAsync(context, rows, ct);

        List<ReclaimableItem> items = [];
        List<PartialJunkItem> partialJunk = [];

        foreach (IGrouping<string, VideoFileScanRow> group in rows.GroupBy(row => row.HostFolder))
        {
            string hostFolder = group.Key;
            VideoFileScanRow firstRow = group.First();

            if (!Ulid.TryParse(firstRow.Share, out Ulid folderId))
            {
                _logger.LogWarning(
                    "[ReclaimScanService] VideoFile share '{Share}' in folder {HostFolder} is not a folder id — skipping",
                    firstRow.Share,
                    hostFolder
                );
                continue;
            }

            if (!driverIdByFolderId.TryGetValue(folderId, out Ulid driverId))
            {
                _logger.LogWarning(
                    "[ReclaimScanService] Folder {FolderId} for {HostFolder} not found — skipping",
                    folderId,
                    hostFolder
                );
                continue;
            }

            bool isProtected;
            VideoFileScanRow servedRow;
            IReadOnlyList<FolderEntry> entries;
            try
            {
                isProtected = group.Any(row => IsServedPlaylist(row.Filename));

                servedRow = group.FirstOrDefault(row => IsServedPlaylist(row.Filename)) ?? firstRow;

                entries = _listFolderEntries(folderId, driverId, hostFolder);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[ReclaimScanService] Could not process {HostFolder} — skipping",
                    hostFolder
                );
                continue;
            }

            ReclaimClassification classification = ReclaimClassifier.Classify(
                entries,
                isProtected,
                now,
                partialStaleAfter
            );

            if (classification.Kind == ReclaimKind.None)
                continue;

            IReadOnlyList<string> targetPaths =
            [
                .. classification.TargetNames.Select(name =>
                    StoragePathHelpers.Combine(hostFolder, name)
                ),
            ];

            if (classification.Kind == ReclaimKind.ReclaimableHls)
            {
                items.Add(
                    new(
                        Id: DeterministicId(hostFolder),
                        Title: ResolveTitle(servedRow, hostFolder),
                        MediaType: ResolveMediaType(servedRow),
                        Folder: hostFolder,
                        ServedCopy: servedRow.Filename,
                        Kind: classification.Kind,
                        TargetPaths: targetPaths,
                        ReclaimableBytes: classification.ReclaimableBytes
                    )
                );
            }
            else
            {
                partialJunk.Add(new(hostFolder, targetPaths, classification.ReclaimableBytes));
            }
        }

        return new(
            items,
            partialJunk,
            items.Sum(item => item.ReclaimableBytes),
            partialJunk.Sum(item => item.Bytes)
        );
    }

    private static async Task<Dictionary<Ulid, Ulid>> ResolveDriverIdsAsync(
        MediaContext context,
        List<VideoFileScanRow> rows,
        CancellationToken ct
    )
    {
        HashSet<Ulid> folderIds = [];
        foreach (VideoFileScanRow row in rows)
        {
            if (Ulid.TryParse(row.Share, out Ulid folderId))
                folderIds.Add(folderId);
        }

        if (folderIds.Count == 0)
            return [];

        List<Folder> folders = await context
            .Folders.AsNoTracking()
            .Where(folder => folderIds.Contains(folder.Id))
            .ToListAsync(ct);

        return folders.ToDictionary(folder => folder.Id, folder => folder.DriverId);
    }

    private TimeSpan ResolvePartialStaleAfter()
    {
        string? raw = _configurationStore.GetValue(PartialStaleHoursKey);
        if (
            !string.IsNullOrWhiteSpace(raw)
            && double.TryParse(
                raw,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out double hours
            )
            && hours > 0
        )
            return TimeSpan.FromHours(hours);

        return TimeSpan.FromHours(DefaultPartialStaleHours);
    }

    private IReadOnlyList<FolderEntry> ListFolderEntriesFromStorage(
        Ulid folderId,
        Ulid driverId,
        string hostFolder
    )
    {
        IStorage storage = _storageFactory.For(folderId, driverId, string.Empty);
        IReadOnlyList<StorageEntry> entries = storage.List(hostFolder, null, recursive: false);

        return
        [
            .. entries.Select(entry => new FolderEntry(
                storage.GetName(entry.Path),
                entry.IsDirectory,
                entry.SizeBytes,
                entry.LastModified
            )),
        ];
    }

    private static string ResolveTitle(VideoFileScanRow row, string hostFolder)
    {
        if (row.MovieId is not null && !string.IsNullOrEmpty(row.MovieTitle))
            return row.MovieTitle;

        if (row.EpisodeId is not null && !string.IsNullOrEmpty(row.ShowTitle))
        {
            return row.SeasonNumber is not null && row.EpisodeNumber is not null
                ? $"{row.ShowTitle} S{row.SeasonNumber:00}E{row.EpisodeNumber:00}"
                : row.ShowTitle;
        }

        return LeafName(hostFolder);
    }

    private static string ResolveMediaType(VideoFileScanRow row)
    {
        if (row.MovieId is not null)
            return MediaTypes.MovieMediaType;

        if (row.EpisodeId is not null)
            return row.ShowMediaType ?? MediaTypes.TvMediaType;

        return "unknown";
    }

    private static string LeafName(string hostFolder)
    {
        string trimmed = hostFolder.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    private static bool IsServedPlaylist(string? filename) =>
        !string.IsNullOrEmpty(filename)
        && filename.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static string DeterministicId(string hostFolder)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(hostFolder));
        return new Ulid([.. hash.AsSpan(0, 16)]).ToString();
    }

    private sealed record VideoFileScanRow(
        string HostFolder,
        string Filename,
        string Share,
        int? MovieId,
        int? EpisodeId,
        string? MovieTitle,
        string? ShowTitle,
        string? ShowMediaType,
        int? SeasonNumber,
        int? EpisodeNumber
    );

    private readonly record struct FreshFolderDbInfo(
        IReadOnlyList<VideoFileScanRow> Rows,
        Ulid FolderId,
        Ulid DriverId
    );
}
