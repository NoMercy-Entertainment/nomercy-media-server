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
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Artists;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs.Dto;
using NoMercy.MediaProcessing.Music;
using NoMercy.MediaProcessing.MusicGenres;
using NoMercy.MediaProcessing.Recordings;
using NoMercy.MediaProcessing.ReleaseGroups;
using NoMercy.MediaProcessing.Releases;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.AcoustId;
using NoMercy.Providers.AcoustId.Client;
using NoMercy.Providers.AcoustId.Models;
using NoMercy.Providers.CoverArt.Client;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Queue.MediaServer;
using NoMercy.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

public class AudioImportJob : AbstractMusicFolderJob
{
    public AudioImportJob() { }

    public AudioImportJob(
        IStorageFactory storageFactory,
        IStorageDriver storageDriver,
        IAudioFingerprinter audioFingerprinter,
        ILoggerFactory loggerFactory
    )
        : base(storageFactory, storageDriver, audioFingerprinter, loggerFactory) { }

    public override string QueueName => "import";

    // Was 6 (above Movie/ShowImportJob's 5): with the "import" queue's
    // priority-DESC ordering, that let a large audio backlog systematically
    // starve video imports indefinitely rather than interleaving with them
    // by creation order. Parity with Movie/ShowImportJob restores fairness.
    public override int Priority => 5;

    private MediaFolderExtend? _rootFolder;

    private MediaContext? _mediaContext;

    public override async Task Handle()
    {
        if (InputFolder.Contains("[Singles]"))
        {
            await ImportSingles();
        }
        else
        {
            await ImportRelease();
        }
    }

    /// <summary>
    /// The release id for a file: the embedded MusicBrainz tag when present,
    /// otherwise whatever acoustic fingerprinting can identify. Returns null only
    /// when both fail, in which case the caller skips the file.
    ///
    /// Both import paths MUST go through this. The singles path fingerprinted
    /// untagged files while the album path silently skipped them, so a library of
    /// releases carrying no MusicBrainz tags (anything not tagged by Picard —
    /// Bandcamp, Free Music Archive, CC albums) imported as literally nothing:
    /// every folder recorded "MusicBrainz release could not be resolved" and the
    /// user saw an empty music library with no error on screen.
    /// </summary>
    private async Task<Guid?> ResolveReleaseIdAsync(MediaFile mediaFile, AudioTagModel audioTag)
    {
        if (
            audioTag.MusicBrainz?.ReleaseId is not null
            && audioTag.MusicBrainz.ReleaseId != Guid.Empty
        )
            return audioTag.MusicBrainz.ReleaseId;

        Guid? discoveredReleaseId = await TryDiscoverReleaseIdAsync(mediaFile);
        if (discoveredReleaseId is null)
            return null;

        audioTag.MusicBrainz ??= new();
        audioTag.MusicBrainz.ReleaseId = discoveredReleaseId.Value;
        return discoveredReleaseId;
    }

    // Identify a file with no embedded MusicBrainz id by acoustic fingerprint:
    // fingerprint via the injected IAudioFingerprinter, look it up against
    // AcoustId, and return the first MusicBrainz release id found (null on any
    // failure, so the caller skips the file rather than crashing the import).
    private async Task<Guid?> TryDiscoverReleaseIdAsync(MediaFile mediaFile)
    {
        try
        {
            using AcoustIdFingerprintClient client = new(AudioFingerprinter);
            AcoustIdFingerprint? result = await client.Lookup(mediaFile.Path);
            if (result is null)
                return null;

            foreach (AcoustIdFingerprintResult fingerprintResult in result.Results)
            {
                foreach (
                    AcoustIdFingerprintRecording? recording in fingerprintResult.Recordings ?? []
                )
                {
                    Guid? releaseId = recording
                        ?.Releases?.FirstOrDefault(release => release.Id != Guid.Empty)
                        ?.Id;
                    if (releaseId is not null && releaseId != Guid.Empty)
                        return releaseId;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.LogInformation(
                "Fingerprint lookup failed for {Path}: {Message}",
                [mediaFile.Path, ex.Message]
            );
            return null;
        }
    }

    private async Task ImportSingles()
    {
        (
            MusicBrainzReleaseClient musicBrainzReleaseClient,
            MusicBrainzArtistClient musicBrainzArtistClient,
            MusicBrainzRecordingClient musicBrainzRecordingClient,
            ReleaseGroupManager releaseGroupManager,
            ReleaseManager releaseManager,
            ArtistManager artistManager,
            RecordingManager recordingManager,
            MusicGenreManager musicGenreManager,
            Library albumLibrary,
            Folder folderLibrary,
            Func<IAsyncEnumerable<(MediaFile MediaFile, AudioTagModel AudioTag)>> audioFilesFactory,
            _,
            JobDispatcher jobDispatcher
        ) = Init();

        using (musicBrainzReleaseClient)
        using (musicBrainzArtistClient)
        using (musicBrainzRecordingClient)
        await using (_mediaContext)
        {
            bool wasEmpty = !await _mediaContext!.AlbumLibrary.AnyAsync(al =>
                al.LibraryId == LibraryId
            );

            Dictionary<
                Guid,
                (
                    MusicBrainzReleaseAppends SingleAppends,
                    List<(MediaFile MediaFile, AudioTagModel audioTagModel)> File
                )
            > processedSingles = new();
            await foreach ((MediaFile mediaFile, AudioTagModel audioTag) in audioFilesFactory())
            {
                if (await ResolveReleaseIdAsync(mediaFile, audioTag) is null)
                    continue;

                MusicBrainzReleaseAppends? releaseAppends =
                    await musicBrainzReleaseClient.WithAllAppends(audioTag.MusicBrainz!.ReleaseId);
                if (releaseAppends is null)
                    continue;

                if (
                    processedSingles.TryGetValue(
                        audioTag.MusicBrainz.ReleaseId,
                        out (
                            MusicBrainzReleaseAppends SingleAppends,
                            List<(MediaFile MediaFile, AudioTagModel audioTagModel)> File
                        ) value
                    )
                )
                {
                    value.File.Add((mediaFile, audioTag));
                }
                else
                {
                    processedSingles.Add(
                        audioTag.MusicBrainz.ReleaseId,
                        (releaseAppends, [(mediaFile, audioTag)])
                    );
                }
            }

            foreach (
                (
                    MusicBrainzReleaseAppends singleRelease,
                    List<(MediaFile mediaFile, AudioTagModel audioTagModel)> files
                ) in processedSingles.Values
            )
            {
                await AddSingleOrRelease(
                    singleRelease,
                    musicGenreManager,
                    releaseGroupManager,
                    releaseManager,
                    albumLibrary,
                    folderLibrary,
                    files,
                    musicBrainzArtistClient,
                    artistManager,
                    jobDispatcher,
                    musicBrainzRecordingClient,
                    recordingManager
                );

                jobDispatcher.DispatchJob<MusicMetadataJob>(singleRelease.MusicBrainzReleaseGroup);
                await SendRefresh(["music", "start"]);
            }

            if (wasEmpty && processedSingles.Count > 0)
                await SendRefresh(["libraries"]);
        }
        try
        {
            musicBrainzReleaseClient.Dispose();
        }
        catch (Exception disposeEx)
        {
            Log.LogError("Dispose failed: {DisposeEx}", disposeEx);
        }
        try
        {
            musicBrainzArtistClient.Dispose();
        }
        catch (Exception disposeEx)
        {
            Log.LogError("Dispose failed: {DisposeEx}", disposeEx);
        }
        try
        {
            musicBrainzRecordingClient.Dispose();
        }
        catch (Exception disposeEx)
        {
            Log.LogError("Dispose failed: {DisposeEx}", disposeEx);
        }
        try
        {
            if (_mediaContext != null)
                await _mediaContext.DisposeAsync();
        }
        catch (Exception disposeEx)
        {
            Log.LogError("Dispose failed: {DisposeEx}", disposeEx);
        }
        _mediaContext = null;
    }

    private async Task ImportRelease()
    {
        (
            MusicBrainzReleaseClient musicBrainzReleaseClient,
            MusicBrainzArtistClient musicBrainzArtistClient,
            MusicBrainzRecordingClient musicBrainzRecordingClient,
            ReleaseGroupManager releaseGroupManager,
            ReleaseManager releaseManager,
            ArtistManager artistManager,
            RecordingManager recordingManager,
            MusicGenreManager musicGenreManager,
            Library albumLibrary,
            Folder folderLibrary,
            Func<IAsyncEnumerable<(MediaFile MediaFile, AudioTagModel AudioTag)>> audioFilesFactory,
            Dictionary<Guid, (MusicBrainzReleaseAppends ReleaseAppends, int Count)> releases,
            JobDispatcher jobDispatcher
        ) = Init();

        using (musicBrainzReleaseClient)
        using (musicBrainzArtistClient)
        using (musicBrainzRecordingClient)
        await using (_mediaContext)
        {
            Log.LogInformation("AudioImportJob: importing {InputFolder}", InputFolder);

            bool wasEmpty = !await _mediaContext!.AlbumLibrary.AnyAsync(al =>
                al.LibraryId == LibraryId
            );

            // Each call to the factory re-scans the folder and rebuilds every AudioTagModel
            // from the file's own tags, so anything ResolveReleaseIdAsync worked out by
            // fingerprint is gone by the next enumeration. Counting in one pass and matching
            // in a second therefore dropped every file on a rip with no MusicBrainz tags —
            // which is the exact case fingerprinting exists for. Resolve once, keep it.
            List<(MediaFile MediaFile, AudioTagModel AudioTag, Guid? ReleaseId)> scannedFiles = [];

            await foreach ((MediaFile mediaFile, AudioTagModel audioTag) in audioFilesFactory())
            {
                Guid? resolvedReleaseId = null;

                if (await ResolveReleaseIdAsync(mediaFile, audioTag) is not null)
                {
                    MusicBrainzReleaseAppends? releaseAppends =
                        await musicBrainzReleaseClient.WithAllAppends(
                            audioTag.MusicBrainz!.ReleaseId
                        );

                    if (releaseAppends is not null)
                    {
                        resolvedReleaseId = releaseAppends.Id;

                        if (
                            releases.TryGetValue(
                                releaseAppends.Id,
                                out (MusicBrainzReleaseAppends ReleaseAppends, int Count) value
                            )
                        )
                            releases[releaseAppends.Id] = (releaseAppends, value.Count + 1);
                        else
                            releases.Add(releaseAppends.Id, (releaseAppends, 1));
                    }
                }

                scannedFiles.Add((mediaFile, audioTag, resolvedReleaseId));
            }

            // pick the most common release
            MusicBrainzReleaseAppends? release = releases
                .OrderByDescending(x => x.Value.Count)
                .FirstOrDefault()
                .Value.ReleaseAppends;
            if (release is null)
            {
                await using MediaContext failureContext = new();
                await ImportFailureRecorder.RecordAsync(
                    failureContext,
                    "AudioImportJob",
                    InputFolder,
                    LibraryId,
                    "MusicBrainz release could not be resolved for this folder."
                );
                return;
            }

            List<(MediaFile MediaFile, AudioTagModel AudioTag)> matchingFiles = scannedFiles
                .Where(scanned =>
                    scanned.ReleaseId == release.Id
                    || scanned.AudioTag.MusicBrainz?.ReleaseId == release.Id
                    || (
                        scanned.AudioTag.MusicBrainz?.ReleaseTrackId != null
                        && release.Media.Any(m =>
                            m.Tracks.Any(t =>
                                t.Id == scanned.AudioTag.MusicBrainz.ReleaseTrackId
                                || t.Id == scanned.AudioTag.MusicBrainz.RecordingId
                                || t.Recording.Id == scanned.AudioTag.MusicBrainz.RecordingId
                                || t.Recording.Id == scanned.AudioTag.MusicBrainz.ReleaseTrackId
                            )
                        )
                    )
                )
                .Select(scanned => (scanned.MediaFile, scanned.AudioTag))
                .ToList();

            await AddSingleOrRelease(
                release,
                musicGenreManager,
                releaseGroupManager,
                releaseManager,
                albumLibrary,
                folderLibrary,
                matchingFiles,
                musicBrainzArtistClient,
                artistManager,
                jobDispatcher,
                musicBrainzRecordingClient,
                recordingManager
            );

            jobDispatcher.DispatchJob<MusicMetadataJob>(release.MusicBrainzReleaseGroup);
            await SendRefresh(["music", "start"]);

            if (wasEmpty)
                await SendRefresh(["libraries"]);
        }
        try
        {
            musicBrainzReleaseClient.Dispose();
        }
        catch (Exception disposeEx)
        {
            Log.LogError("Dispose failed: {DisposeEx}", disposeEx);
        }
        try
        {
            musicBrainzArtistClient.Dispose();
        }
        catch (Exception disposeEx)
        {
            Log.LogError("Dispose failed: {DisposeEx}", disposeEx);
        }
        try
        {
            musicBrainzRecordingClient.Dispose();
        }
        catch (Exception disposeEx)
        {
            Log.LogError("Dispose failed: {DisposeEx}", disposeEx);
        }
        try
        {
            if (_mediaContext != null)
                await _mediaContext.DisposeAsync();
        }
        catch (Exception disposeEx)
        {
            Log.LogError("Dispose failed: {DisposeEx}", disposeEx);
        }
        _mediaContext = null;
    }

    private static async Task SendRefresh(object?[] query)
    {
        if (EventBusProvider.IsConfigured)
            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent { QueryKey = query }
            );
    }

    private async Task AddSingleOrRelease(
        MusicBrainzReleaseAppends release,
        MusicGenreManager musicGenreManager,
        ReleaseGroupManager releaseGroupManager,
        ReleaseManager releaseManager,
        Library albumLibrary,
        Folder folderLibrary,
        List<(MediaFile MediaFile, AudioTagModel AudioTag)> audioFiles,
        MusicBrainzArtistClient musicBrainzArtistClient,
        ArtistManager artistManager,
        JobDispatcher jobDispatcher,
        MusicBrainzRecordingClient musicBrainzRecordingClient,
        RecordingManager recordingManager
    )
    // Storing a release needs one of its files to anchor the album to, and the old
    // .First() threw "Sequence contains no elements" when the folder scan came back
    // empty — a message that names neither the folder nor the release, which is what
    // made a failed music import unreadable in the queue.
    {
        CoverArtImageManagerManager.CoverPalette? coverPalette =
            await CoverArtImageManagerManager.Add(release.MusicBrainzReleaseGroup.Id, true);
        if (coverPalette is not null)
        {
            using Image<Rgba32>? downloadedImage = await CoverArtCoverArtClient.Download(
                coverPalette.Url
            );
        }

        await AddGenres(release.Genres, musicGenreManager);

        await releaseGroupManager.Store(release.MusicBrainzReleaseGroup, LibraryId, coverPalette);

        if (audioFiles.Count == 0)
        {
            Log.LogWarning(
                "AudioImportJob: no audio files resolved under {InputFolder} for release {Title} ({ReleaseId}); nothing to store",
                InputFolder,
                release.Title,
                release.Id
            );
            return;
        }

        Log.LogInformation(
            "AudioImportJob: {Count} file(s) matched {Title}",
            audioFiles.Count,
            release.Title
        );

        await releaseManager.Store(
            release,
            albumLibrary,
            folderLibrary,
            audioFiles.First().MediaFile,
            coverPalette
        );

        foreach (ReleaseArtistCredit artistCredit in release.ArtistCredit)
        {
            MusicBrainzArtistAppends? artistDetails = await musicBrainzArtistClient.WithAllAppends(
                artistCredit.MusicBrainzArtist.Id
            );
            if (artistDetails is null)
                continue;
            await artistManager.Store(artistDetails, release, albumLibrary, folderLibrary);
            jobDispatcher.DispatchJob<MusicMetadataJob>(artistDetails);
            await SendRefresh(["music", "artist", artistDetails.Id]);
        }

        List<MusicBrainzTrack> allTracks = release.Media.SelectMany(m => m.Tracks).ToList();
        Dictionary<Guid, MediaFile> resolvedFileByTrackId = ResolveFilesForTracks(
            allTracks,
            audioFiles
        );

        foreach (MusicBrainzTrack musicBrainzTrack in allTracks)
        {
            if (!resolvedFileByTrackId.TryGetValue(musicBrainzTrack.Id, out MediaFile? mediaFile))
                continue;

            if (musicBrainzTrack.Recording.Id == Guid.Empty)
            {
                Log.LogWarning(
                    "AudioImportJob: track {Track} in release {ReleaseId} has no recording id; skipping recording lookup",
                    musicBrainzTrack.Title,
                    release.Id
                );
                continue;
            }

            MusicBrainzRecordingAppends? musicBrainzRecording =
                await musicBrainzRecordingClient.WithAllAppends(musicBrainzTrack.Recording.Id);
            if (musicBrainzRecording is null)
                continue;

            await AddGenres(musicBrainzRecording.Genres, musicGenreManager);

            await recordingManager.Store(
                release,
                musicBrainzTrack,
                [],
                mediaFile,
                folderLibrary,
                coverPalette
            );

            // Analysis is part of the import, not a switch to find afterwards.
            // It belongs here rather than on LibraryScanCompletedEvent: a music
            // scan dispatches these import jobs and then announces itself, so at
            // that moment no Track row exists yet and a brand-new library would
            // wait for the hourly sweep. The library's own AnalyzeAudio is the
            // opt-out, the same way AutoEncodeOnScan gates the encode below.
            AudioAnalysisDispatch.AfterStore(jobDispatcher, albumLibrary, musicBrainzTrack.Id);

            // Same per-library opt-in AutoEncodeSubscriber already enforces for
            // video — a preset assignment is "the preset to use when I encode",
            // not "re-encode everything the moment it is scanned". Without this
            // gate music had no opt-out at all: every import, including an
            // already-playable MP3, was unconditionally transcoded through
            // ffmpeg regardless of the library's own auto-encode setting.
            if (!albumLibrary.AutoEncodeOnScan)
            {
                Log.LogDebug(
                    "Auto-encode-on-scan is off for library {LibraryId}; {Track} from {File} stored, encode skipped",
                    albumLibrary.Id,
                    musicBrainzTrack.Title,
                    mediaFile.Path
                );
            }
            else
            {
                Log.LogInformation(
                    "AudioImportJob: encoding {Track} from {File}",
                    musicBrainzTrack.Title,
                    mediaFile.Path
                );

                await MusicEncodeDispatcher.Dispatch(
                    StorageFactory,
                    albumLibrary,
                    folderLibrary,
                    release,
                    musicBrainzTrack,
                    mediaFile,
                    InputFolder
                );
            }

            foreach (MusicBrainzArtistCredit artistCredit in musicBrainzRecording.ArtistCredit)
            {
                if (_rootFolder is null)
                    continue;
                MusicBrainzArtistAppends? artistDetails =
                    await musicBrainzArtistClient.WithAllAppends(artistCredit.MusicBrainzArtist.Id);
                if (artistDetails is null)
                    continue;
                await artistManager.Store(
                    artistDetails,
                    albumLibrary,
                    folderLibrary,
                    _rootFolder!,
                    musicBrainzTrack
                );
                jobDispatcher.DispatchJob<MusicMetadataJob>(artistDetails);
                await SendRefresh(["music", "artist", artistDetails.Id]);
            }
        }

        await SendRefresh(["music", "album", release.Id]);
    }

    /// <summary>
    /// Which physical file, if any, IS each release track.
    /// <para>
    /// Pass 1: an embedded MusicBrainz tag id is the one signal precise enough to
    /// trust outright, so every track gets first refusal on it before any fuzzy
    /// signal runs — otherwise a loosely-matching earlier track could claim the
    /// file a later track actually had an exact id match for.
    /// </para>
    /// <para>
    /// Pass 2: title / duration / position fallback for whatever pass 1 left
    /// unresolved (a rip with no MusicBrainz tags fails every id comparison above),
    /// restricted to files no earlier track already claimed. A file can satisfy this
    /// loose OR for several different tracks — a soundtrack's short, similarly-timed
    /// cues especially — and previously the same file was handed to every one of
    /// them, producing a separate Track row under a different title for each, all
    /// pointing at the one physical file.
    /// </para>
    /// </summary>
    internal static Dictionary<Guid, MediaFile> ResolveFilesForTracks(
        List<MusicBrainzTrack> allTracks,
        List<(MediaFile MediaFile, AudioTagModel AudioTag)> audioFiles
    )
    {
        HashSet<MediaFile> claimedFiles = [];
        Dictionary<Guid, MediaFile> resolvedFileByTrackId = new();

        foreach (MusicBrainzTrack musicBrainzTrack in allTracks)
        {
            foreach ((MediaFile file, AudioTagModel tag) in audioFiles)
            {
                if (claimedFiles.Contains(file))
                    continue;

                bool exactMatch =
                    tag.MusicBrainz?.ReleaseTrackId == musicBrainzTrack.Id
                    || tag.MusicBrainz?.ReleaseTrackId == musicBrainzTrack.Recording.Id
                    || tag.MusicBrainz?.RecordingId == musicBrainzTrack.Id
                    || tag.MusicBrainz?.RecordingId == musicBrainzTrack.Recording.Id;

                if (!exactMatch)
                    continue;

                resolvedFileByTrackId[musicBrainzTrack.Id] = file;
                claimedFiles.Add(file);
                break;
            }
        }

        foreach (MusicBrainzTrack musicBrainzTrack in allTracks)
        {
            if (resolvedFileByTrackId.ContainsKey(musicBrainzTrack.Id))
                continue;

            foreach ((MediaFile file, AudioTagModel tag) in audioFiles)
            {
                if (claimedFiles.Contains(file))
                    continue;

                bool fuzzyMatch =
                    musicBrainzTrack.Title.ContainsSanitized(tag.Tags?.Title ?? file.Parsed?.Title)
                    || Math.Abs(tag.Duration - musicBrainzTrack.Duration) < 5
                    || musicBrainzTrack.Position == tag.Tags?.Track
                    || musicBrainzTrack.Position == file.Parsed?.TrackNumber;

                if (!fuzzyMatch)
                    continue;

                resolvedFileByTrackId[musicBrainzTrack.Id] = file;
                claimedFiles.Add(file);
                break;
            }
        }

        return resolvedFileByTrackId;
    }

    private static async Task AddGenres(
        MusicBrainzGenreDetails[] genres,
        MusicGenreManager musicGenreManager
    )
    {
        foreach (MusicBrainzGenreDetails musicBrainzGenreDetails in genres)
            await musicGenreManager.Store(musicBrainzGenreDetails);
    }

    private AudioImportContext Init()
    {
        _mediaContext = new();
        JobDispatcher jobDispatcher = new();
        MusicBrainzReleaseClient musicBrainzReleaseClient = new();
        MusicBrainzArtistClient musicBrainzArtistClient = new();
        MusicBrainzRecordingClient musicBrainzRecordingClient = new();
        Dictionary<Guid, (MusicBrainzReleaseAppends ReleaseAppends, int Count)> releases = new();

        ReleaseGroupRepository releaseGroupRepository = new(_mediaContext);
        ReleaseGroupManager releaseGroupManager = new(
            releaseGroupRepository,
            jobDispatcher,
            LoggerFactory.CreateLogger<ReleaseGroupManager>()
        );

        MusicGenreRepository musicGenreRepository = new(_mediaContext);
        MusicGenreManager musicGenreManager = new(musicGenreRepository);

        ReleaseRepository releaseRepository = new(_mediaContext);
        ReleaseManager releaseManager = new(
            releaseRepository,
            musicGenreRepository,
            StorageFactory,
            jobDispatcher,
            LoggerFactory.CreateLogger<ReleaseManager>()
        );

        ArtistRepository artistRepository = new(_mediaContext);
        ArtistManager artistManager = new(
            artistRepository,
            musicGenreRepository,
            jobDispatcher,
            StorageFactory,
            LoggerFactory.CreateLogger<ArtistManager>()
        );

        RecordingRepository recordingRepository = new(_mediaContext);
        RecordingManager recordingManager = new(
            recordingRepository,
            musicGenreRepository,
            artistRepository,
            StorageDriver,
            StorageFactory,
            LoggerFactory.CreateLogger<RecordingManager>()
        );

        Library albumLibrary = _mediaContext
            .Libraries.Where(f => f.Id == LibraryId)
            .Include(f => f.FolderLibraries)
                .ThenInclude(f => f.Folder)
            .First();
        Folder folderLibrary = albumLibrary.FolderLibraries.First().Folder;
        Func<IAsyncEnumerable<(MediaFile MediaFile, AudioTagModel AudioTag)>> audioFilesFactory =
            GetAudioFiles;

        return new(
            musicBrainzReleaseClient,
            musicBrainzArtistClient,
            musicBrainzRecordingClient,
            releaseGroupManager,
            releaseManager,
            artistManager,
            recordingManager,
            musicGenreManager,
            albumLibrary,
            folderLibrary,
            audioFilesFactory,
            releases,
            jobDispatcher
        );
    }

    private async IAsyncEnumerable<(MediaFile MediaFile, AudioTagModel AudioTag)> GetAudioFiles()
    {
        // Depth 2, matching the file list that produced the release the operator chose.
        // An album folder holds its tracks directly, and at depth 1 the scan returned no
        // files for the very folder the file list had just read eleven out of — so the
        // import had nothing to anchor the release to and stored nothing.
        await using MediaScan mediaScan = new(StorageDriver);
        ConcurrentBag<MediaFolderExtend> rootFolders = await mediaScan
            .DisableRegexFilter()
            .EnableFileListing()
            .Process(InputFolder, 2);

        _rootFolder ??= rootFolders.FirstOrDefault();

        IEnumerable<MediaFile> files = rootFolders.SelectMany(mediaFolderExtend =>
            mediaFolderExtend.Files ?? Enumerable.Empty<MediaFile>()
        );

        ConcurrentBag<(MediaFile, AudioTagModel)> bag = [];

        foreach (MediaFile mediaFile in files)
        {
            AudioTagModel audioTagModel = await AudioTagModel.Create(mediaFile);
            bag.Add((mediaFile, audioTagModel));
        }

        foreach ((MediaFile mediaFile, AudioTagModel audioTagModel) in bag)
            yield return (mediaFile, audioTagModel);
    }
}

public record AudioImportContext(
    MusicBrainzReleaseClient MusicBrainzReleaseClient,
    MusicBrainzArtistClient MusicBrainzArtistClient,
    MusicBrainzRecordingClient MusicBrainzRecordingClient,
    ReleaseGroupManager ReleaseGroupManager,
    ReleaseManager ReleaseManager,
    ArtistManager ArtistManager,
    RecordingManager RecordingManager,
    MusicGenreManager MusicGenreManager,
    Library AlbumLibrary,
    Folder FolderLibrary,
    Func<IAsyncEnumerable<(MediaFile MediaFile, AudioTagModel AudioTag)>> AudioFilesFactory,
    Dictionary<Guid, (MusicBrainzReleaseAppends ReleaseAppends, int Count)> Releases,
    JobDispatcher JobDispatcher
);
