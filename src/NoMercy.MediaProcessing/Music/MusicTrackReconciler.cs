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
using NoMercy.Database;
using NoMercy.MediaProcessing.Recordings;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Music;

/// <summary>
/// Brings the Track rows of a music library folder in step with what is on disk, whoever
/// changed the disk: a file that moved is repointed, a file that vanished is marked
/// unavailable and removed once the grace period has passed, a file that came back is
/// cleared. Nothing is marked or removed when the listing looks wrong.
/// </summary>
public class MusicTrackReconciler(
    MediaContext mediaContext,
    ILogger logger,
    IRecordingIdentifier? recordingIdentifier = null,
    MusicListingCache? listingCache = null,
    ITrackFileReader? fileReader = null
)
{
    private const int MaxFingerprintsPerPass = 200;
    private const int MaxTagReadsPerPass = 200;
    private const int DurationToleranceSeconds = 2;

    private static readonly string[] AudioExtensions = [".mp3", ".flac", ".opus", ".wav", ".m4a"];

    private readonly MusicListingCache _cache = listingCache ?? MusicListingCache.Shared;
    private readonly ITrackFileReader _fileReader = fileReader ?? new TagLibTrackFileReader();
    private readonly MissingTrackPruner _pruner = new(mediaContext, logger);

    private readonly record struct TrackRow(
        Guid Id,
        string HostFolder,
        string Filename,
        DateTime? MissingSince,
        int TrackNumber,
        string Duration
    )
    {
        public string Path => HostFolder.TrimEnd('/') + Filename;
    }

    private static readonly MusicReconcileResult Nothing = new(0, 0, 0, 0, false);

    private sealed class PassCounts
    {
        public int Listed { get; set; }
        public int Skipped { get; set; }
    }

    public async Task<MusicReconcileResult> ReconcileAsync(
        Ulid folderId,
        string scanRoot,
        IStorageDriver driver,
        IReadOnlyCollection<string>? onlyUnder = null,
        DateTime? nowUtc = null,
        CancellationToken cancellationToken = default,
        bool force = false
    )
    {
        PassCounts counts = new();
        MusicReconcileResult result = await ReconcileCoreAsync(
            folderId,
            scanRoot,
            driver,
            onlyUnder,
            nowUtc,
            force,
            counts,
            cancellationToken
        );

        logger.LogInformation(
            "Reconciled music tracks of '{Root}': {Listed} folder(s) listed, {Skipped} skipped, {Repointed} repointed, {Marked} marked unavailable, {Cleared} cleared, {Removed} removed, {GuardSkips} guard skip(s)",
            scanRoot,
            counts.Listed,
            counts.Skipped,
            result.Moved,
            result.Marked,
            result.Cleared,
            result.Removed,
            result.DeletesSkipped ? 1 : 0
        );
        return result;
    }

    private async Task<MusicReconcileResult> ReconcileCoreAsync(
        Ulid folderId,
        string scanRoot,
        IStorageDriver driver,
        IReadOnlyCollection<string>? onlyUnder,
        DateTime? nowUtc,
        bool force,
        PassCounts counts,
        CancellationToken cancellationToken
    )
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;

        if (!driver.DirectoryExists(scanRoot))
        {
            logger.LogWarning(
                "Not reconciling tracks: library folder root '{Root}' is not readable",
                scanRoot
            );
            return Nothing with { DeletesSkipped = true };
        }

        List<TrackFileEntry> previousListing = [];
        List<TrackFileEntry> currentListing = [];
        List<string> unreadable = [];
        foreach (
            string root in onlyUnder is null
                ? [scanRoot]
                : onlyUnder.Where(driver.DirectoryExists).ToList()
        )
        {
            MusicListing listing = _cache.List(driver, root, force);
            previousListing.AddRange(listing.Previous);
            currentListing.AddRange(listing.Current);
            counts.Listed += listing.ListedFolders;
            foreach ((string directory, Exception error) in listing.SkippedFolders)
            {
                unreadable.Add(directory);
                logger.LogWarning(
                    error,
                    "Skipping unreadable music folder '{Folder}'; its tracks stay as they are",
                    directory
                );
            }
        }

        counts.Skipped = unreadable.Count;

        // A track under a folder that could not be read is held: nothing says it is gone.
        List<TrackRow> rows =
        [
            .. (await LoadRowsAsync(folderId, onlyUnder, cancellationToken)).Where(row =>
                !unreadable.Any(directory => IsUnder(row.HostFolder, directory))
            ),
        ];
        Dictionary<string, List<TrackRow>> rowsByPath = rows.GroupBy(row => row.Path)
            .ToDictionary(group => group.Key, group => group.ToList());

        if (currentListing.Count == 0 && rowsByPath.Count > 0)
        {
            logger.LogWarning(
                "Not reconciling tracks of '{Root}': the listing came back empty",
                scanRoot
            );
            return Nothing with { DeletesSkipped = true };
        }

        Dictionary<string, TrackFileEntry> previousByPath = previousListing
            .GroupBy(entry => entry.Path)
            .ToDictionary(group => group.Key, group => group.First());
        List<TrackFileEntry> known =
        [
            .. rowsByPath.Keys.Select(path =>
            {
                TrackRow first = rowsByPath[path][0];
                return previousByPath.TryGetValue(path, out TrackFileEntry? seen)
                    ? seen
                    : new TrackFileEntry(first.HostFolder, first.Filename);
            }),
        ];

        MusicListingDiff.Result diff = MusicListingDiff.Diff(known, currentListing);

        List<TrackFileEntry> gone = [];
        HashSet<string> presentButUnlisted = [];
        foreach (TrackFileEntry entry in diff.Gone)
        {
            if (driver.FileExists(entry.Path))
                presentButUnlisted.Add(entry.Path);
            else
                gone.Add(entry);
        }

        List<(TrackFileEntry From, TrackFileEntry To)> moves = [.. diff.Moves];
        await MatchByContentAsync(driver, diff.Added, gone, rowsByPath, moves, cancellationToken);
        await MatchByTagsAsync(driver, diff.Added, gone, rowsByPath, moves, cancellationToken);

        int moved = await ApplyMovesAsync(folderId, scanRoot, rowsByPath, moves, cancellationToken);

        HashSet<string> present =
        [
            .. currentListing.Select(entry => entry.Path),
            .. presentButUnlisted,
        ];
        int cleared = await _pruner.ClearMissingAsync(
            [
                .. rows.Where(row => row.MissingSince is not null && present.Contains(row.Path))
                    .Select(row => row.Id),
            ],
            cancellationToken
        );

        HashSet<string> movedFrom = [.. moves.Select(move => move.From.Path)];
        List<TrackFileEntry> missing = [.. gone.Where(entry => !movedFrom.Contains(entry.Path))];

        if (MissingFileGuard.ShouldSkipDeletes(known.Count, missing.Count))
        {
            logger.LogWarning(
                "Not marking tracks missing in '{Root}': {Missing} of {Known} known files are gone in one pass",
                scanRoot,
                missing.Count,
                known.Count
            );
            return new(moved, 0, cleared, 0, true);
        }

        int marked = await _pruner.MarkMissingAsync(
            [.. missing.SelectMany(entry => rowsByPath[entry.Path]).Select(row => row.Id)],
            now,
            cancellationToken
        );

        DateTime cutoff = now - MissingTrackPruner.GracePeriod;
        (int removed, _) = await _pruner.RemoveUnheldAsync(
            [
                .. rows.Where(row =>
                        row.MissingSince is { } since
                        && since <= cutoff
                        && !present.Contains(row.Path)
                        && !movedFrom.Contains(row.Path)
                    )
                    .Select(row => row.Id),
            ],
            cancellationToken
        );

        return new(moved, marked, cleared, removed, false);
    }

    private async Task<List<TrackRow>> LoadRowsAsync(
        Ulid folderId,
        IReadOnlyCollection<string>? onlyUnder,
        CancellationToken cancellationToken
    )
    {
        List<TrackRow> stored = await mediaContext
            .Tracks.AsNoTracking()
            .Where(track =>
                track.FolderId == folderId
                && track.HostFolder != null
                && track.HostFolder != ""
                && track.Filename != null
                && track.Filename != ""
            )
            .Select(track => new TrackRow(
                track.Id,
                track.HostFolder!,
                track.Filename!,
                track.MissingSince,
                track.TrackNumber,
                track.Duration
            ))
            .ToListAsync(cancellationToken);

        await NormalizeStoredHostFoldersAsync(folderId, stored, cancellationToken);
        List<TrackRow> rows =
        [
            .. stored.Select(row =>
                row with
                {
                    HostFolder = MusicListingCache.Normalize(row.HostFolder),
                }
            ),
        ];

        if (onlyUnder is null)
            return rows;

        List<string> prefixes = [.. onlyUnder.Select(MusicListingCache.Normalize)];
        return [.. rows.Where(row => prefixes.Any(prefix => IsUnder(row.HostFolder, prefix)))];
    }

    private static bool IsUnder(string hostFolder, string directory) =>
        hostFolder == directory || hostFolder.StartsWith(directory + "/", StringComparison.Ordinal);

    // An old row can hold backslashes or a trailing slash. RepointTrackFile matches the
    // normalized form, so a row left in its old form could never be repointed.
    private async Task NormalizeStoredHostFoldersAsync(
        Ulid folderId,
        List<TrackRow> stored,
        CancellationToken cancellationToken
    )
    {
        foreach (
            string rawHostFolder in stored
                .Select(row => row.HostFolder)
                .Where(host => host != MusicListingCache.Normalize(host))
                .Distinct()
        )
        {
            string normalized = MusicListingCache.Normalize(rawHostFolder);
            await mediaContext
                .Tracks.Where(track =>
                    track.FolderId == folderId && track.HostFolder == rawHostFolder
                )
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(track => track.HostFolder, normalized),
                    cancellationToken
                );
        }
    }

    // The listing could not match these files; only now is their content read.
    private async Task MatchByContentAsync(
        IStorageDriver driver,
        IReadOnlyList<TrackFileEntry> added,
        List<TrackFileEntry> gone,
        Dictionary<string, List<TrackRow>> rowsByPath,
        List<(TrackFileEntry From, TrackFileEntry To)> moves,
        CancellationToken cancellationToken
    )
    {
        if (recordingIdentifier is null || gone.Count == 0)
            return;

        List<TrackFileEntry> candidates =
        [
            .. added.Where(IsAudioFile).Take(MaxFingerprintsPerPass),
        ];

        foreach (TrackFileEntry candidate in candidates)
        {
            if (gone.Count == 0)
                return;

            IReadOnlyCollection<Guid> recordings = await recordingIdentifier.IdentifyAsync(
                driver,
                candidate.Path,
                cancellationToken
            );
            if (recordings.Count == 0)
                continue;

            TrackFileEntry? match = gone.FirstOrDefault(entry =>
                rowsByPath[entry.Path].Any(row => recordings.Contains(row.Id))
            );
            if (match is null)
                continue;

            moves.Add((match, candidate));
            gone.Remove(match);
        }
    }

    private static bool IsAudioFile(TrackFileEntry entry) =>
        AudioExtensions.Contains(
            Path.GetExtension(entry.Filename),
            StringComparer.OrdinalIgnoreCase
        );

    // A file renamed in place has no listing identity and may carry new tags. Track number and
    // length stand in for it, and only a one-to-one fit in the same folder is trusted.
    private async Task MatchByTagsAsync(
        IStorageDriver driver,
        IReadOnlyList<TrackFileEntry> added,
        List<TrackFileEntry> gone,
        Dictionary<string, List<TrackRow>> rowsByPath,
        List<(TrackFileEntry From, TrackFileEntry To)> moves,
        CancellationToken cancellationToken
    )
    {
        if (gone.Count == 0)
            return;

        HashSet<string> taken = [.. moves.Select(move => move.To.Path)];
        HashSet<string> goneFolders = [.. gone.Select(entry => entry.HostFolder)];
        List<TrackFileEntry> candidates =
        [
            .. added
                .Where(entry =>
                    IsAudioFile(entry)
                    && !taken.Contains(entry.Path)
                    && goneFolders.Contains(entry.HostFolder)
                )
                .Take(MaxTagReadsPerPass),
        ];

        List<(TrackFileEntry Candidate, List<TrackFileEntry> Fits)> fits = [];
        foreach (TrackFileEntry candidate in candidates)
        {
            if (await IsOwnedByRowAsync(candidate, cancellationToken))
                continue;

            TrackFileFacts? facts = await _fileReader.ReadAsync(
                driver,
                candidate.Path,
                cancellationToken
            );
            if (facts is null || facts.TrackNumber <= 0)
                continue;

            List<TrackFileEntry> fitting =
            [
                .. gone.Where(entry =>
                    entry.HostFolder == candidate.HostFolder
                    && rowsByPath[entry.Path].All(row => Fits(row, facts))
                ),
            ];
            if (fitting.Count > 0)
                fits.Add((candidate, fitting));
        }

        foreach ((TrackFileEntry candidate, List<TrackFileEntry> fitting) in fits)
        {
            if (fitting.Count != 1)
                continue;

            TrackFileEntry from = fitting[0];
            if (fits.Count(other => other.Fits.Contains(from)) != 1)
                continue;

            logger.LogWarning(
                "Linking renamed file '{To}' to the missing track row(s) of '{From}': same track number, length within {Seconds} s",
                candidate.Path,
                from.Path,
                DurationToleranceSeconds
            );
            moves.Add((from, candidate));
            gone.Remove(from);
        }
    }

    private static bool Fits(TrackRow row, TrackFileFacts facts)
    {
        int rowSeconds = row.Duration.ToSeconds();
        return row.TrackNumber == facts.TrackNumber
            && rowSeconds > 0
            && Math.Abs(rowSeconds - facts.Duration.TotalSeconds) <= DurationToleranceSeconds;
    }

    private Task<bool> IsOwnedByRowAsync(
        TrackFileEntry file,
        CancellationToken cancellationToken
    ) =>
        mediaContext
            .Tracks.AsNoTracking()
            .AnyAsync(
                track => track.HostFolder == file.HostFolder && track.Filename == file.Filename,
                cancellationToken
            );

    private async Task<int> ApplyMovesAsync(
        Ulid folderId,
        string scanRoot,
        Dictionary<string, List<TrackRow>> rowsByPath,
        List<(TrackFileEntry From, TrackFileEntry To)> moves,
        CancellationToken cancellationToken
    )
    {
        RecordingRepository repository = new(mediaContext);
        int moved = 0;

        foreach ((TrackFileEntry from, TrackFileEntry to) in moves.ToList())
        {
            if (
                !NoMercy.Storage.StoragePathHelpers.TryGetLibraryRelativeParts(
                    to.Path,
                    scanRoot,
                    out string folder,
                    out string filename
                )
            )
            {
                moves.Remove((from, to));
                continue;
            }

            int repointed = await repository.RepointTrackFile(
                from.HostFolder,
                from.Filename,
                new(folderId, folder, to.HostFolder, filename)
            );
            moved += repointed;

            await _pruner.ClearMissingAsync(
                [.. rowsByPath[from.Path].Select(row => row.Id)],
                cancellationToken
            );
            logger.LogInformation(
                "Repointed {Count} track row(s) from '{From}' to '{To}'",
                repointed,
                from.Path,
                to.Path
            );
        }

        return moved;
    }
}
