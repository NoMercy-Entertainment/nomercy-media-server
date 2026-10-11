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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Artists;
using NoMercy.MediaProcessing.Jobs.Dto;
using NoMercy.MediaProcessing.MusicGenres;
using NoMercy.MediaProcessing.Recordings;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.AcoustId;
using NoMercy.Providers.AcoustId.Client;
using NoMercy.Providers.AcoustId.Models;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

/// <summary>
/// Re-derives which physical file each track of one already-imported release
/// actually IS, and corrects the stored rows in place.
/// <para>
/// <see cref="AudioImportJob.AddSingleOrRelease"/> used to let several different
/// MusicBrainz tracks all claim the same physical file (fixed in
/// <see cref="AudioImportJob.ResolveFilesForTracks"/>), so a release imported before
/// that fix has its <c>Tracks</c> rows still pointing several different track ids at
/// one file each. This job re-scans the release's folder, re-runs the corrected
/// matcher, and re-stores every resolved (track, file) pair — an update to the
/// SAME track ids <see cref="RecordingManager.Store"/> already owns, not a new
/// import. It deliberately never dispatches an encode: the files this walks are the
/// library's own source files, already playable, and the bug was in which title got
/// attached to which of them — not in the audio itself.
/// </para>
/// </summary>
public class MusicTrackRepairJob : AbstractMusicFolderJob
{
    public MusicTrackRepairJob() { }

    public MusicTrackRepairJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        IAudioFingerprinter audioFingerprinter,
        ILoggerFactory loggerFactory
    )
        : base(storageFactory, storageDriver, audioFingerprinter, loggerFactory) { }

    public override string QueueName => "import";
    public override int Priority => 6;

    public override async Task Handle()
    {
        await using MediaContext mediaContext = new();

        using MusicBrainzReleaseClient musicBrainzReleaseClient = new(ReleaseId);
        MusicBrainzReleaseAppends? release;
        try
        {
            release = await musicBrainzReleaseClient.WithAllAppends();
        }
        catch (HttpRequestException ex)
        {
            // MusicBrainz rate-limits aggressively, and a repair batch can dispatch
            // hundreds of these at once. A 429/503 here is transient — the row is
            // left exactly as it was (safe, just still unrepaired), and re-running
            // the repair endpoint later picks it back up since it is idempotent.
            Log.LogWarning(
                "MusicTrackRepairJob: MusicBrainz lookup failed for release {ReleaseId} ({InputFolder}): {Message}",
                ReleaseId,
                InputFolder,
                ex.Message
            );
            return;
        }

        if (release is null)
        {
            Log.LogWarning(
                "MusicTrackRepairJob: MusicBrainz release {ReleaseId} could not be resolved for {InputFolder}",
                ReleaseId,
                InputFolder
            );
            return;
        }

        Library albumLibrary = await mediaContext
            .Libraries.Where(f => f.Id == LibraryId)
            .Include(f => f.FolderLibraries)
                .ThenInclude(f => f.Folder)
            .FirstAsync();
        Folder folderLibrary = albumLibrary.FolderLibraries.First().Folder;

        List<(MediaFile MediaFile, AudioTagModel AudioTag)> audioFiles = [];
        await foreach ((MediaFile mediaFile, AudioTagModel audioTag) in GetAudioFiles())
            audioFiles.Add((mediaFile, audioTag));

        if (audioFiles.Count == 0)
        {
            Log.LogWarning(
                "MusicTrackRepairJob: no audio files found under {InputFolder}; nothing to repair",
                InputFolder
            );
            return;
        }

        List<MusicBrainzTrack> allTracks = release.Media.SelectMany(m => m.Tracks).ToList();

        await EnrichUntaggedFilesByFingerprint(audioFiles, allTracks);

        Dictionary<Guid, MediaFile> resolvedFileByTrackId = AudioImportJob.ResolveFilesForTracks(
            allTracks,
            audioFiles
        );

        RecordingRepository recordingRepository = new(mediaContext);
        MusicGenreRepository musicGenreRepository = new(mediaContext);
        ArtistRepository artistRepository = new(mediaContext);
        RecordingManager recordingManager = new(
            recordingRepository,
            musicGenreRepository,
            artistRepository,
            StorageDriver,
            StorageFactory,
            LoggerFactory.CreateLogger<RecordingManager>()
        );

        // A track stored without its artists shows no artist anywhere (2026-10-07: 14 tracks
        // this job had re-matched had none), so each one is stored with the artists it credits.
        MusicBrainzArtistClient musicBrainzArtistClient = new();
        Dictionary<Guid, MusicBrainzArtistAppends?> artistsById = [];

        int repaired = 0;
        foreach (MusicBrainzTrack track in allTracks)
        {
            if (!resolvedFileByTrackId.TryGetValue(track.Id, out MediaFile? mediaFile))
                continue;

            List<MusicBrainzArtistAppends> artists = [];
            foreach (
                Guid artistId in track
                    .ArtistCredit.Select(credit => credit.MusicBrainzArtist.Id)
                    .Distinct()
            )
            {
                if (!artistsById.TryGetValue(artistId, out MusicBrainzArtistAppends? artist))
                {
                    artist = await musicBrainzArtistClient.WithAllAppends(artistId);
                    artistsById[artistId] = artist;
                }

                if (artist is not null)
                    artists.Add(artist);
            }

            await recordingManager.Store(
                release,
                track,
                [.. artists],
                mediaFile,
                folderLibrary,
                null
            );
            repaired++;
        }

        Log.LogInformation(
            "MusicTrackRepairJob: re-resolved {Repaired}/{Total} track(s) for {Title} under {InputFolder}",
            repaired,
            allTracks.Count,
            release.Title,
            InputFolder
        );
    }

    /// <summary>
    /// The title/duration fallback in <see cref="AudioImportJob.ResolveFilesForTracks"/> is
    /// only as good as the file's own name and tags — a rip with neither a MusicBrainz tag
    /// NOR reliable title/duration data (the exact state that produced this album's swap:
    /// several tracks with no embedded MusicBrainz id at all) has no signal left for it to
    /// use and repeats whatever wrong guess made the mismatch in the first place. AcoustID
    /// identifies a file from its audio content directly, so it survives that: a fingerprint
    /// match against a recording that is actually IN this release sets the file's
    /// RecordingId, giving the exact-match pass real evidence instead of another guess.
    /// Restricted to files the tag already failed for, and to recordings this release
    /// actually contains, so a fingerprint false positive from a cover or a live version
    /// on AcoustID can't misfile a track.
    /// </summary>
    private async Task EnrichUntaggedFilesByFingerprint(
        List<(MediaFile MediaFile, AudioTagModel AudioTag)> audioFiles,
        List<MusicBrainzTrack> allTracks
    )
    {
        HashSet<Guid> recordingIdsInRelease = [.. allTracks.Select(track => track.Recording.Id)];

        foreach ((MediaFile mediaFile, AudioTagModel audioTag) in audioFiles)
        {
            bool alreadyIdentified =
                (audioTag.MusicBrainz?.ReleaseTrackId ?? Guid.Empty) != Guid.Empty
                || (audioTag.MusicBrainz?.RecordingId ?? Guid.Empty) != Guid.Empty;

            if (alreadyIdentified)
                continue;

            try
            {
                using AcoustIdFingerprintClient client = new(AudioFingerprinter);
                AcoustIdFingerprint? result = await client.Lookup(mediaFile.Path);
                if (result is null)
                    continue;

                Guid matchedRecordingId = result
                    .Results.SelectMany(r => r.Recordings ?? [])
                    .Select(recording => recording?.Id ?? Guid.Empty)
                    .FirstOrDefault(recordingId => recordingIdsInRelease.Contains(recordingId));

                if (matchedRecordingId == Guid.Empty)
                    continue;

                audioTag.MusicBrainz ??= new();
                audioTag.MusicBrainz.RecordingId = matchedRecordingId;

                Log.LogInformation(
                    "MusicTrackRepairJob: fingerprint-identified {Path} as recording {RecordingId}",
                    mediaFile.Path,
                    matchedRecordingId
                );
            }
            catch (Exception ex)
            {
                Log.LogWarning(
                    "MusicTrackRepairJob: fingerprint lookup failed for {Path}: {Message}",
                    mediaFile.Path,
                    ex.Message
                );
            }
        }
    }

    private async IAsyncEnumerable<(MediaFile MediaFile, AudioTagModel AudioTag)> GetAudioFiles()
    {
        await using MediaScan mediaScan = new(StorageDriver);
        ConcurrentBag<MediaFolderExtend> rootFolders = await mediaScan
            .DisableRegexFilter()
            .EnableFileListing()
            .Process(InputFolder, 2);

        IEnumerable<MediaFile> files = rootFolders.SelectMany(mediaFolderExtend =>
            mediaFolderExtend.Files ?? Enumerable.Empty<MediaFile>()
        );

        foreach (MediaFile mediaFile in files)
            yield return (mediaFile, await AudioTagModel.Create(mediaFile));
    }
}
