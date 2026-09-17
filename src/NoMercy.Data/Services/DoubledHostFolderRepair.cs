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
using NoMercy.Database.Models.Music;
using NoMercy.Storage;

namespace NoMercy.Data.Services;

/// <summary>
/// Repairs the tracks an older importer run stored with a <c>HostFolder</c>
/// that holds the album folder twice — on Windows once with forward slashes and
/// once with backslashes, on Linux the same rooted path simply repeated.
/// <c>Filename</c> is the bare file name, so every consumer that combines the
/// two (playback, subtitles, transcodes, the audio analysis job, the plugin
/// audio tools) builds a path that exists nowhere, and the analysis of such a
/// track fails on every sweep, for good.
/// <para>
/// A row is only rewritten when the two halves are the same folder, the stored
/// path really addresses nothing, <i>and</i> the file is where the repaired
/// folder says it is. A guess that moved a row onto a path nobody checked would
/// be worse than the broken value it replaced. Anything else is counted and left
/// exactly as it was.
/// </para>
/// <para>
/// A repaired track also has its analysis verdict reset to
/// <see cref="AudioAnalysisState.Pending"/> in the same unit of work. Those
/// tracks carry a <c>Failed</c> verdict at the current analyzer version, which
/// both the sweep query and the job's own "already done" check read as an
/// answer; without the reset the row would be correct and still never analysed.
/// </para>
/// <para>
/// Libraries on a remote driver are left alone in practice: their
/// <c>HostFolder</c> is a driver key, and the existence check here is answered
/// by the local driver, which will not find such a key and so never rewrites the
/// row. A local library is where the broken shape came from.
/// </para>
/// <para>
/// Idempotent: a repaired row is no longer doubled, so a second run does not
/// rewrite it. A healthy database costs one query, one table scan and no disk
/// access at all.
/// </para>
/// </summary>
public class DoubledHostFolderRepair(
    IDbContextFactory<MediaContext> mediaContextFactory,
    IStorageDriver storageDriver,
    ILogger<DoubledHostFolderRepair> logger
) : IDoubledHostFolderRepair
{
    /// <summary>
    /// How many rows may be named in the log before the rest are summarised.
    /// A library that went wrong wholesale must not turn one boot into tens of
    /// thousands of warnings.
    /// </summary>
    private const int MaxNamedRows = 20;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await using MediaContext mediaContext = await mediaContextFactory.CreateDbContextAsync(
            cancellationToken
        );

        // The Linux shape carries no marker a database can filter on — a path
        // repeated is just a longer path — so every stored folder is read and
        // judged in memory. One query, no disk access until a row looks wrong.
        List<TrackPath> candidates = await mediaContext
            .Tracks.AsNoTracking()
            .Where(track => track.HostFolder != null && track.HostFolder != "")
            .Select(track => new TrackPath(track.Id, track.HostFolder!, track.Filename))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> repairs = [];
        int leftAloneStoredPathResolves = 0;
        int leftAloneFileMissing = 0;
        int leftAloneHalvesDiffer = 0;
        int leftAloneErrored = 0;
        int warningsNamed = 0;
        int warningsSuppressed = 0;

        void Warn(string message, params object?[] arguments)
        {
            if (warningsNamed < MaxNamedRows)
            {
                warningsNamed++;
                logger.LogWarning(message, arguments);
                return;
            }

            warningsSuppressed++;
        }

        foreach (TrackPath candidate in candidates)
        {
            // Stop between rows rather than in the middle of one, and keep what
            // was decided so far: a shutdown must not cost the whole sweep.
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                string? singleFolder = HostFolderPath.RepairDoubled(candidate.HostFolder);
                if (singleFolder is null)
                {
                    // No second root either means the value is simply fine.
                    if (!HostFolderPath.ContainsSecondRoot(candidate.HostFolder))
                        continue;

                    leftAloneHalvesDiffer++;
                    Warn(
                        "Track {TrackId} keeps a host folder holding two different rooted paths: '{HostFolder}'",
                        candidate.Id,
                        candidate.HostFolder
                    );
                    continue;
                }

                string filename = candidate.Filename ?? string.Empty;

                // A doubled host folder addresses nothing; one that does address
                // a file is a folder that merely looks repeated — a library at
                // "/music/music" is a real thing — and must not be touched.
                if (
                    storageDriver.FileExists(
                        storageDriver.CombinePath(candidate.HostFolder, filename)
                    )
                )
                {
                    leftAloneStoredPathResolves++;
                    continue;
                }

                string repairedPath = storageDriver.CombinePath(singleFolder, filename);
                if (!storageDriver.FileExists(repairedPath))
                {
                    leftAloneFileMissing++;
                    Warn(
                        "Track {TrackId} keeps its doubled host folder: nothing is at '{Path}'",
                        candidate.Id,
                        repairedPath
                    );
                    continue;
                }

                repairs[candidate.Id] = singleFolder;
            }
            // A driver that throws on one path — a share that went away, a
            // permission it lacks — must cost that row and no other.
            catch (Exception exception)
            {
                leftAloneErrored++;
                Warn(
                    "Track {TrackId} left alone: {Error} while checking its file",
                    candidate.Id,
                    exception.GetType().Name
                );
            }
        }

        int verdictsReset = 0;

        // The save is short and bounded, and it is why the loop above stops
        // early: cancelling it too would throw the decided repairs away and
        // turn the early stop into the very loss it exists to prevent, so the
        // token is deliberately not passed on.
        if (repairs.Count > 0)
        {
            verdictsReset = await ApplyAsync(mediaContext, repairs);
        }

        if (warningsSuppressed > 0)
            logger.LogWarning(
                "Doubled host folder sweep: {Count} further rows left alone, not named here",
                warningsSuppressed
            );

        if (
            repairs.Count > 0
            || leftAloneStoredPathResolves > 0
            || leftAloneFileMissing > 0
            || leftAloneHalvesDiffer > 0
            || leftAloneErrored > 0
        )
            logger.LogInformation(
                "Doubled host folder sweep: {Repaired} repaired, {VerdictsReset} analysis verdicts reset, "
                    + "{StoredPathResolves} left alone because the stored path resolves, "
                    + "{FileMissing} left alone because the repaired path does not, "
                    + "{HalvesDiffer} left alone because the halves differ, "
                    + "{Errored} left alone after an error",
                [
                    repairs.Count,
                    verdictsReset,
                    leftAloneStoredPathResolves,
                    leftAloneFileMissing,
                    leftAloneHalvesDiffer,
                    leftAloneErrored,
                ]
            );

        return repairs.Count;
    }

    /// <summary>
    /// Writes the repaired folders and resets the analysis verdict of every
    /// repaired track in one save. Both halves belong together: a row whose path
    /// is right but whose verdict still says Failed at this analyzer version is
    /// never selected again, so the repair would fix the symptom the owner sees
    /// and not the one he reported.
    /// </summary>
    private static async Task<int> ApplyAsync(
        MediaContext mediaContext,
        Dictionary<Guid, string> repairs
    )
    {

        List<Guid> trackIds = [.. repairs.Keys];

        List<Track> tracks = await mediaContext
            .Tracks.Where(track => trackIds.Contains(track.Id))
            .ToListAsync();

        foreach (Track track in tracks)
            track.HostFolder = repairs[track.Id];

        List<TrackAudioAnalysis> verdicts = await mediaContext
            .TrackAudioAnalysis.Where(analysis =>
                trackIds.Contains(analysis.TrackId) && analysis.State != AudioAnalysisState.Pending
            )
            .ToListAsync();

        foreach (TrackAudioAnalysis verdict in verdicts)
        {
            verdict.State = AudioAnalysisState.Pending;
            verdict.FailureReason = null;
        }

        await mediaContext.SaveChangesAsync();

        return verdicts.Count;
    }

    /// <summary>The three stored columns the decision needs, and nothing else.</summary>
    private sealed record TrackPath(Guid Id, string HostFolder, string? Filename);
}
