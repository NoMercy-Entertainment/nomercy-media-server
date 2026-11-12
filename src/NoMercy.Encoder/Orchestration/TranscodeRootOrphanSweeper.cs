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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;

namespace NoMercy.Encoder.Orchestration;

/// <summary>
/// One-shot hosted service that runs once at startup and deletes batch-encode
/// working directories left behind under <see cref="StoragePaths.TranscodeRoot"/>
/// by a previous server crash or unclean shutdown. Mirrors
/// <see cref="NoMercy.Encoder.LiveTranscode.LiveTranscodeOrphanSweeper"/>, which
/// covers the separate live-transcode cache.
///
/// <para>
/// <see cref="EncodingOrchestrator"/> creates either an opaque
/// <c>nomercy-enc-&lt;ulid&gt;</c> dir or an output-mirrored <c>&lt;Show&gt;…</c>
/// dir under the transcode root, and removes it when the encode completes. A
/// crash mid-encode leaves the dir forever, slowly filling the cache volume.
/// At boot no encode is in flight, so every leftover is an orphan.
/// </para>
///
/// <para>
/// Safety: the root is only swept wholesale when it is the dedicated managed
/// encoder cache (<see cref="AppFiles.EncoderCachePath"/>). If the root is still
/// the unconfigured <see cref="Path.GetTempPath"/> default, deletion is limited
/// to the <c>nomercy-enc-*</c> prefix so unrelated system-temp content is never
/// touched — the mirrored show dirs are sacrificed in that fallback rather than
/// risk a stray delete outside our own scratch.
/// </para>
/// </summary>
public class TranscodeRootOrphanSweeper : IHostedService
{
    internal const string EncWorkingDirPrefix = "nomercy-enc-";

    /// <summary>
    /// A directory with any file written within this window survives the
    /// sweep even if it matches the orphan pattern. "No encode is in flight
    /// at boot" is only true after a crash — a deliberate restart during a
    /// live encode (dev iteration, an update) leaves a directory that is
    /// still being written to by the ffmpeg process that just got killed, and
    /// whose queue row will resume it on the very next wake-up. Sweeping that
    /// away wholesale (recursive delete of the whole show, not just the
    /// current episode) destroyed real, unfinished progress on every restart
    /// of a long batch — the actual incident this guard exists to prevent.
    /// </summary>
    internal static readonly TimeSpan MinOrphanAge = TimeSpan.FromMinutes(15);

    private readonly ILogger<TranscodeRootOrphanSweeper> _logger;
    private readonly IStorage _storage;
    private readonly string _root;
    private readonly bool _sweepAllChildren;
    private readonly TimeSpan _minOrphanAge;
    private readonly IEncoderTempDirectoryOwners? _owners;

    public TranscodeRootOrphanSweeper(
        ILogger<TranscodeRootOrphanSweeper> logger,
        IStorage storage,
        IEncoderTempDirectoryOwners? owners = null
    )
        : this(
            logger,
            storage,
            StoragePaths.TranscodeRoot,
            IsDedicatedEncoderCache(StoragePaths.TranscodeRoot),
            MinOrphanAge,
            owners
        ) { }

    internal TranscodeRootOrphanSweeper(
        ILogger<TranscodeRootOrphanSweeper> logger,
        IStorage storage,
        string root,
        bool sweepAllChildren,
        TimeSpan minOrphanAge,
        IEncoderTempDirectoryOwners? owners = null
    )
    {
        _logger = logger;
        _storage = storage;
        _root = root;
        _sweepAllChildren = sweepAllChildren;
        _minOrphanAge = minOrphanAge;
        _owners = owners;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_storage.Exists(_root))
        {
            _logger.LogDebug(
                "TranscodeRootOrphanSweeper: transcode root {Dir} does not exist, nothing to sweep",
                _root
            );
            return;
        }

        IReadOnlyCollection<string> ownedDirs = [];
        if (_owners is not null)
        {
            try
            {
                ownedDirs = await _owners.GetOwnedTempDirectoriesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "TranscodeRootOrphanSweeper: could not read the encoder queue, "
                        + "keeping every working dir rather than risk deleting a live job's output"
                );
                return;
            }
        }

        string pattern = _sweepAllChildren ? "*" : EncWorkingDirPrefix + "*";

        IReadOnlyList<StorageEntry> orphans;
        try
        {
            orphans = _storage
                .List(_root, pattern, recursive: false)
                .Where(entry => entry.IsDirectory)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TranscodeRootOrphanSweeper: could not enumerate {Dir}", _root);
            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _minOrphanAge;

        foreach (StorageEntry entry in orphans)
        {
            if (IsOwned(entry.Path, ownedDirs))
            {
                _logger.LogInformation(
                    "TranscodeRootOrphanSweeper: {Dir} belongs to a queued encoder job, keeping it",
                    entry.Path
                );
                continue;
            }

            DateTimeOffset? newestWrite = NewestWriteTime(entry.Path);
            if (newestWrite is { } newest && newest > cutoff)
            {
                _logger.LogDebug(
                    "TranscodeRootOrphanSweeper: {Dir} was written to at {NewestWrite}, "
                        + "inside the {MinAge} grace window — not an orphan, skipping",
                    entry.Path,
                    newest,
                    _minOrphanAge
                );
                continue;
            }

            try
            {
                _storage.DeleteDirectory(entry.Path, recursive: true);
                _logger.LogInformation(
                    "TranscodeRootOrphanSweeper: deleted orphan {Dir}",
                    entry.Path
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "TranscodeRootOrphanSweeper: could not delete orphan {Dir}",
                    entry.Path
                );
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsOwned(string entryPath, IReadOnlyCollection<string> ownedDirs)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string entryFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entryPath));

        foreach (string owned in ownedDirs)
        {
            string ownedFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(owned));
            if (
                ownedFull.Equals(entryFull, comparison)
                || ownedFull.StartsWith(entryFull + Path.DirectorySeparatorChar, comparison)
            )
                return true;
        }

        return false;
    }

    /// <summary>
    /// The most recent write time among every file under <paramref name="path"/>,
    /// recursive. A directory's own timestamp only moves when an entry is
    /// added or removed directly inside it — not when ffmpeg appends to a
    /// segment file three levels down — so the directory's own
    /// <see cref="StorageEntry.LastModified"/> cannot answer "is anything in
    /// here still being written to."
    /// </summary>
    private DateTimeOffset? NewestWriteTime(string path)
    {
        try
        {
            return _storage
                .List(path, null, recursive: true)
                .Where(entry => !entry.IsDirectory)
                .Select(entry => (DateTimeOffset?)entry.LastModified)
                .Max();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "TranscodeRootOrphanSweeper: could not read write times under {Dir}, "
                    + "treating as recent (skip) rather than risk deleting live output",
                path
            );
            return DateTimeOffset.UtcNow;
        }
    }

    private static bool IsDedicatedEncoderCache(string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string normalizedCache = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppFiles.EncoderCachePath)
        );
        return string.Equals(
            normalizedRoot,
            normalizedCache,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );
    }
}
