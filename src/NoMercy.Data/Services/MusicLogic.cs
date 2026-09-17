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
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Data.Jobs;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Music;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Music;
using NoMercy.NmSystem;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.FFProbe;
using NoMercy.Providers.AcoustId;
using NoMercy.Providers.AcoustId.Client;
using NoMercy.Providers.AcoustId.Models;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Queue.MediaServer;
using NoMercy.Storage;
using NoMercyQueue;

namespace NoMercy.Data.Services;

public partial class MusicLogic : IAsyncDisposable
{
    private readonly IDbContextFactory<MediaContext> _mediaContextFactory;
    private readonly IStorageFactory _storageFactory;
    private readonly IAudioFingerprinter _audioFingerprinter;
    private ConcurrentBag<MediaFile>? Files { get; set; }
    private MediaFolderExtend ListPath { get; set; }

    private int Year { get; set; }
    private string AlbumName { get; set; }
    private string ArtistName { get; set; }
    private Library Library { get; set; }
    private Folder? Folder { get; set; }

    private readonly ILogger<MusicLogic> _logger;

    public MusicLogic(
        ILogger<MusicLogic> logger,
        Library library,
        MediaFolderExtend listPath,
        IDbContextFactory<MediaContext> mediaContextFactory,
        IStorageFactory storageFactory,
        IAudioFingerprinter audioFingerprinter
    )
    {
        _logger = logger;
        _mediaContextFactory = mediaContextFactory;
        _storageFactory = storageFactory;
        _audioFingerprinter = audioFingerprinter;
        Library = library;
        ListPath = listPath;

        Files = listPath.Files;

        Match match = PathRegex().Match(listPath.Path);
        ArtistName = match.Groups["artist"].Success ? match.Groups["artist"].Value : string.Empty;
        AlbumName = match.Groups["album"].Success ? match.Groups["album"].Value : string.Empty;
        Year = match.Groups["year"].Success ? Convert.ToInt32(match.Groups["year"].Value) : 1970;

        // Post-migration: Folder.Path = "" (sub-path inside driver root). Match
        // by resolving each folder's driver root and comparing against the scan path.
        Folder = Library
            .FolderLibraries.Select(folderLibrary => folderLibrary.Folder)
            .FirstOrDefault(folder =>
            {
                IStorage folderStorage = _storageFactory.For(
                    folder.Id,
                    folder.DriverId,
                    string.Empty
                );
                // Resolve through the driver, not the IStorage facade: the facade's
                // GetFullPath is a LocalStorage-only escape hatch that throws on every
                // remote backend, so a facade call here killed folder matching for
                // NFS / SMB / S3 / WebDAV music libraries.
                string driverRoot = folderStorage.Driver.GetFullPath(folder.Path);
                return listPath.Path.StartsWith(driverRoot, StringComparison.OrdinalIgnoreCase);
            });

        _logger.LogTrace("Files");
        _logger.LogTrace("{Files}", Files ?? []);

        _logger.LogTrace("ArtistName: {ArtistName}", ArtistName);
        _logger.LogTrace("AlbumName {AlbumName}", AlbumName);
        _logger.LogTrace("Year: {Year}", Year);

        _logger.LogTrace("Folder: {Path}", Folder?.Path);
    }

    public async Task Process()
    {
        _logger.LogTrace("Processing Folder: {Path}", Folder?.Path);
        await Parallel.ForEachAsync(
            Files ?? [],
            SystemParallelism.Options,
            async (file, cancellationToken) =>
            {
                await using MediaContext mediaContext =
                    await _mediaContextFactory.CreateDbContextAsync(cancellationToken);

                try
                {
                    _logger.LogDebug("Analyzing File: {Name}", file.Name);
                    FfProbeData ffProbeData = await FfProbe.CreateAsync(
                        file.Path,
                        cancellationToken
                    );

                    AcoustIdFingerprintRecording? fingerPrintRecording = await MatchTrack(
                        file,
                        ffProbeData
                    );
                    if (fingerPrintRecording is not null)
                    {
                        foreach (
                            AcoustIdFingerprintReleaseGroups release in fingerPrintRecording.Releases
                                ?? []
                        )
                        {
                            if (release.TrackCount == null || release.TrackCount != Files?.Count)
                            {
                                _logger.LogTrace("Track Count Mismatch: {Title}", release.Title);
                                return;
                            }

                            try
                            {
                                await ProcessRelease(
                                    mediaContext,
                                    release,
                                    file,
                                    fingerPrintRecording
                                );
                            }
                            catch (Exception e)
                            {
                                if (e.Message.Contains("404"))
                                    return;
                                _logger.LogError(e.Message);
                            }
                        }

                        return;
                    }

                    AcoustIdFingerprintReleaseGroups? fallbackParsedResult = FallbackParser(
                        file,
                        ffProbeData
                    );
                    if (fallbackParsedResult is null)
                        return;

                    await ProcessRelease(mediaContext, fallbackParsedResult, file);
                }
                catch (Exception e)
                {
                    if (e.Message.Contains("404"))
                        return;
                    _logger.LogError(e.Message);
                }
            }
        );
    }

    private async Task ProcessRelease(
        MediaContext mediaContext,
        AcoustIdFingerprintReleaseGroups release,
        MediaFile mediaFile,
        AcoustIdFingerprintRecording? matchedRecording = null
    )
    {
        _logger.LogTrace("Processing release: {Title} with id: {Id}", [release.Title, release.Id]);

        using MusicBrainzReleaseClient musicBrainzReleaseClient = new(release.Id);

        MusicBrainzReleaseAppends? releaseAppends = await musicBrainzReleaseClient.WithAllAppends();

        if (releaseAppends is null || string.IsNullOrEmpty(releaseAppends.Title))
        {
            _logger.LogWarning("Release not found: {Title}", release.Title);
            await Task.CompletedTask;
            return;
        }

        if (await StoreReleaseGroups(mediaContext, releaseAppends) is null)
            _logger.LogTrace(
                "Release Group already exists: {Title}",
                releaseAppends.MusicBrainzReleaseGroup.Title
            );
        // await Task.CompletedTask;
        // return;
        else
            _logger.LogDebug(
                "Processing release: {Title} with id: {Id}",
                [release.Title, release.Id]
            );

        if (await StoreRelease(mediaContext, releaseAppends, mediaFile) is null)
            _logger.LogTrace("Release already exists: {Title}", releaseAppends.Title);
        // await Task.CompletedTask;
        // return;
        await LinkReleaseToReleaseGroup(mediaContext, releaseAppends);
        await LinkReleaseToLibrary(mediaContext, releaseAppends);

        MusicBrainzTrack? encodableTrack = ResolveTrackForFile(
            releaseAppends,
            mediaFile,
            matchedRecording
        );

        if (encodableTrack is null)
            _logger.LogWarning(
                "No track on {Release} matched {File}, so it was stored but never encoded",
                releaseAppends.Title,
                mediaFile.Path
            );

        foreach (MusicBrainzMedia media in releaseAppends.Media)
        foreach (MusicBrainzTrack track in media.Tracks)
        {
            if (await StoreTrack(mediaContext, releaseAppends, track, media, mediaFile) is null)
                continue;

            await LinkTrackToRelease(mediaContext, track, releaseAppends);

            if (encodableTrack is not null && track.Id == encodableTrack.Id)
                await DispatchEncode(releaseAppends, track, mediaFile);

            foreach (ReleaseArtistCredit artist in track.ArtistCredit)
            {
                await StoreArtist(mediaContext, artist.MusicBrainzArtist);
                await LinkArtistToTrack(mediaContext, artist.MusicBrainzArtist, track);

                await LinkArtistToAlbum(mediaContext, artist.MusicBrainzArtist, releaseAppends);
                await LinkArtistToLibrary(mediaContext, artist.MusicBrainzArtist);

                await LinkArtistToReleaseGroup(
                    mediaContext,
                    releaseAppends,
                    artist.MusicBrainzArtist.Id
                );
            }
        }

        await Task.CompletedTask;
    }

    private async Task<AcoustIdFingerprintRecording?> MatchTrack(
        MediaFile file,
        FfProbeData ffProbeData
    )
    {
        _logger.LogTrace("Matching Track: {Name}", file.Name);

        AcoustIdFingerprint? lookupResult;
        try
        {
            AcoustIdFingerprintClient acoustIdFingerprintClient = new(_audioFingerprinter);
            lookupResult = await acoustIdFingerprintClient.Lookup(file.Path);
            acoustIdFingerprintClient.Dispose();
            if (lookupResult is null)
                return null;
        }
        catch (Exception e)
        {
            _logger.LogError(e.Message);
            throw;
        }

        AcoustIdFingerprintRecording? fingerPrintRecording = null;

        foreach (AcoustIdFingerprintResult fingerPrint in lookupResult.Results ?? [])
        {
            _logger.LogTrace("Matching Recording: {Id}", fingerPrint.Id);
            foreach (AcoustIdFingerprintRecording? recording in fingerPrint.Recordings ?? [])
            {
                if (recording?.Releases is null)
                    continue;

                fingerPrintRecording = MatchRelease(file, recording, ffProbeData);

                if (fingerPrintRecording is not null)
                    break;

                fingerPrintRecording = MatchRelease(file, recording, ffProbeData, false);
            }
        }

        return fingerPrintRecording;
    }

    /// <summary>
    /// Which single track of the release this one file IS.
    /// <para>
    /// The fingerprint answers it outright when it identified a recording, because a
    /// MusicBrainz track carries the recording id it was pressed from. Only when the
    /// fingerprint found nothing does this fall back to the less certain reading of the
    /// file's own tags — the track number the file claims, then its title.
    /// </para>
    /// <para>
    /// Returning null is a real answer: the file was not identified, so it is stored but
    /// never encoded. Guessing here would write a file to disk under another track's name.
    /// </para>
    /// </summary>
    private static MusicBrainzTrack? ResolveTrackForFile(
        MusicBrainzReleaseAppends release,
        MediaFile file,
        AcoustIdFingerprintRecording? matchedRecording
    ) => MusicEncodeDispatcher.ResolveTrackForFile(release, file, matchedRecording);

    /// <summary>
    /// Hands the identified track to the encoder, which resolves the destination folder's
    /// presets and asks the model for its own sanitized filename fragment
    /// (<see cref="Track.CreateTitle"/>) rather than assembling a path here.
    /// </summary>
    private async Task DispatchEncode(
        MusicBrainzReleaseAppends release,
        MusicBrainzTrack track,
        MediaFile file
    )
    {
        if (Folder is null)
        {
            _logger.LogWarning(
                "No destination folder on this import, so {Track} from {File} was stored but never encoded",
                track.Title,
                file.Path
            );
            return;
        }

        // Same per-library opt-in AutoEncodeSubscriber already enforces for video —
        // a preset assignment is "the preset to use when I encode", not "re-encode
        // everything the moment it is scanned". Without this gate music had no
        // opt-out at all: every import, including an already-playable MP3, was
        // unconditionally transcoded through ffmpeg regardless of the library's
        // own auto-encode setting.
        if (!Library.AutoEncodeOnScan)
        {
            _logger.LogDebug(
                "Auto-encode-on-scan is off for library {LibraryId}; {Track} from {File} stored, encode skipped",
                Library.Id,
                track.Title,
                file.Path
            );
            return;
        }

        await MusicEncodeDispatcher.Dispatch(
            _storageFactory,
            Library,
            Folder,
            release,
            track,
            file,
            ListPath.Path
        );
    }

    private AcoustIdFingerprintReleaseGroups? FallbackParser(
        MediaFile file,
        FfProbeData ffProbeData
    )
    {
        _logger.LogTrace("Fallback Parser: {Name}", file.Name);
        string? albumId = ffProbeData
            .Format.Tags?.FirstOrDefault(t => t.Key == "MusicBrainz Album Id")
            .Value;

        _logger.LogTrace("AlbumId: {AlbumId}", albumId);

        if (albumId is null)
            return null;

        return new() { Id = albumId.ToGuid() };
    }

    private AcoustIdFingerprintRecording? MatchRelease(
        MediaFile file,
        AcoustIdFingerprintRecording? recording,
        FfProbeData ffProbeData,
        bool strictMatch = true
    )
    {
        _logger.LogTrace("Matching Release: {Title}", recording?.Title);
        if (recording is null)
            return null;

        AcoustIdFingerprintRecording? fingerPrintRecording = null;

        foreach (AcoustIdFingerprintReleaseGroups release in recording.Releases ?? [])
        {
            bool matchesTrackCount =
                release.TrackCount != null && release.TrackCount == Files?.Count;
            if (!matchesTrackCount)
                continue;

            string fileNameSanitized =
                file.Parsed?.Title?.RemoveDiacritics().RemoveNonAlphaNumericCharacters()
                ?? string.Empty;
            string recordNameSanitized = (
                recording.Title?.RemoveDiacritics().RemoveNonAlphaNumericCharacters()
            ).OrEmpty();
            bool matchesName =
                !fileNameSanitized.Equals(string.Empty)
                && !recordNameSanitized.Equals(string.Empty)
                && fileNameSanitized.Contains(recordNameSanitized);

            // var ffProbeData = FFProbe.AnalyseAsync(file.Path).Result;
            double fileDuration = ffProbeData.Format.Duration.TotalSeconds;
            double recordDuration = recording.Duration;
            bool matchesDuration =
                fileDuration > 0
                && recordDuration > 0
                && Math.Abs(recordDuration - fileDuration) < 10;

            if (strictMatch && matchesName && matchesDuration)
            {
                fingerPrintRecording = recording;
                break;
            }

            if (!matchesName && !matchesDuration)
                continue;
            fingerPrintRecording = recording;
            break;
        }

        return fingerPrintRecording;
    }

    private static string MakeArtistFolder(string artist)
    {
        string artistName = artist.RemoveDiacritics();

        string artistFolder = char.IsNumber(artistName[0])
            ? "#"
            : artistName[0].ToString().ToUpper();

        return $"/{artistFolder}/{artistName}";
    }

    private async Task<MusicBrainzReleaseAppends?> StoreReleaseGroups(
        MediaContext mediaContext,
        MusicBrainzReleaseAppends musicBrainzRelease
    )
    {
        _logger.LogTrace(
            "Storing Release Group: {Title}",
            musicBrainzRelease.MusicBrainzReleaseGroup.Title
        );

        bool hasReleaseGroup = mediaContext
            .ReleaseGroups.AsNoTracking()
            .Any(r => r.Id == musicBrainzRelease.MusicBrainzReleaseGroup.Id);

        if (hasReleaseGroup)
            return null;

        ReleaseGroup insert = new()
        {
            Id = musicBrainzRelease.MusicBrainzReleaseGroup.Id,
            Title = musicBrainzRelease.MusicBrainzReleaseGroup.Title,
            Description = string.IsNullOrEmpty(
                musicBrainzRelease.MusicBrainzReleaseGroup.Disambiguation
            )
                ? null
                : musicBrainzRelease.MusicBrainzReleaseGroup.Disambiguation,
            Year = musicBrainzRelease.MusicBrainzReleaseGroup.FirstReleaseDate.ParseYear(),
            LibraryId = Library.Id,
        };

        try
        {
            await mediaContext
                .ReleaseGroups.Upsert(insert)
                .On(e => new { e.Id })
                .WhenMatched(
                    (s, i) =>
                        new()
                        {
                            Id = i.Id,
                            Title = i.Title,
                            Description = i.Description,
                            Year = i.Year,
                            LibraryId = i.LibraryId,
                        }
                )
                .RunAsync();

            foreach (
                MusicBrainzGenreDetails genre in musicBrainzRelease.MusicBrainzReleaseGroup.Genres
                    ?? []
            )
                await LinkGenreToReleaseGroup(
                    mediaContext,
                    musicBrainzRelease.MusicBrainzReleaseGroup,
                    genre
                );

            MusicMetadataJob musicDescriptionJob = new(musicBrainzRelease.MusicBrainzReleaseGroup);
            QueueRunner.Current!.Dispatcher.Dispatch(musicDescriptionJob);
        }
        catch (Exception e)
        {
            _logger.LogError(e.Message);
            return null;
        }

        _logger.LogTrace(
            "Release Group stored: {Title}",
            musicBrainzRelease.MusicBrainzReleaseGroup.Title
        );
        return musicBrainzRelease;
    }

    private async Task<MusicBrainzReleaseAppends?> StoreRelease(
        MediaContext mediaContext,
        MusicBrainzReleaseAppends musicBrainzRelease,
        MediaFile mediaFile
    )
    {
        _logger.LogTrace("Storing Release: {Title}", musicBrainzRelease.Title);
        MusicBrainzMedia? media = musicBrainzRelease.Media.FirstOrDefault(m => m.Tracks.Length > 0);
        if (media is null)
            return null;

        bool hasAlbum = mediaContext
            .Albums.AsNoTracking()
            .Any(a => a.Id == musicBrainzRelease.Id && a.Cover != null);

        if (hasAlbum)
            return musicBrainzRelease;

        StoragePathHelpers.TryGetLibraryRelativeParts(
            mediaFile.Path,
            ResolveLibraryRoot(),
            out string relativeFolder,
            out _
        );
        string folder =
            StoragePathHelpers.GetParent(mediaFile.Path.Replace('\\', '/')) ?? string.Empty;

        Album insert = new()
        {
            Id = musicBrainzRelease.Id,
            Name = musicBrainzRelease.Title,
            TitleSort = musicBrainzRelease.Title.TitleSort(),
            Country = musicBrainzRelease.Country,
            Disambiguation = string.IsNullOrEmpty(musicBrainzRelease.Disambiguation)
                ? null
                : musicBrainzRelease.Disambiguation,
            Year = musicBrainzRelease.ResolvedYear() ?? 0,
            Tracks = media.Tracks.Length,

            LibraryId = Library.Id,
            FolderId = Folder!.Id,
            Folder = relativeFolder,
            HostFolder = folder.PathName(),
        };

        try
        {
            await mediaContext
                .Albums.Upsert(insert)
                .On(e => new { e.Id })
                .WhenMatched(
                    (s, i) =>
                        new()
                        {
                            Id = i.Id,
                            Name = i.Name,
                            TitleSort = i.TitleSort,
                            Disambiguation = i.Disambiguation,
                            Description = i.Description,
                            Year = i.Year,
                            Country = i.Country,
                            Tracks = i.Tracks,
                            LibraryId = i.LibraryId,
                            Folder = i.Folder,
                            FolderId = i.FolderId,
                            HostFolder = i.HostFolder,
                        }
                )
                .RunAsync();

            foreach (MusicBrainzGenreDetails genre in musicBrainzRelease.Genres)
                await LinkGenreToRelease(mediaContext, musicBrainzRelease, genre);

            CoverArtImageJob coverArtImageJob = new(musicBrainzRelease);
            QueueRunner.Current!.Dispatcher.Dispatch(coverArtImageJob);

            FanArtImagesJob fanartImagesJob = new(musicBrainzRelease);
            QueueRunner.Current!.Dispatcher.Dispatch(fanartImagesJob);

            if (EventBusProvider.IsConfigured)
                await EventBusProvider.Current.PublishAsync(
                    new LibraryRefreshedEvent
                    {
                        QueryKey = ["music", "album", musicBrainzRelease.Id.ToString()],
                    }
                );
        }
        catch (Exception e)
        {
            _logger.LogError(e.Message);
            return null;
        }

        _logger.LogTrace("Release stored: {Title}", musicBrainzRelease.Title);

        return musicBrainzRelease;
    }

    private async Task StoreArtist(
        MediaContext mediaContext,
        MusicBrainzArtistDetails musicBrainzArtist
    )
    {
        _logger.LogTrace("Processing Artist: {Name}", musicBrainzArtist.Name);

        bool hasArtist = mediaContext.Artists.AsNoTracking().Any(a => a.Id == musicBrainzArtist.Id);

        if (hasArtist)
            return;

        string artistFolder = MakeArtistFolder(musicBrainzArtist.Name);
        Artist insert = new()
        {
            Id = musicBrainzArtist.Id,
            Name = musicBrainzArtist.Name,
            Disambiguation = string.IsNullOrEmpty(musicBrainzArtist.Disambiguation)
                ? null
                : musicBrainzArtist.Disambiguation,
            Country = musicBrainzArtist.Country,
            // Use the same display-order sort title as TMDB titles: strip a leading
            // article and keep first-name-first. MusicBrainz's SortName inverts people
            // to surname-first ("Belle, Tony"), which files them under the wrong letter
            // in the A-Z index versus the name shown on the card.
            TitleSort = musicBrainzArtist.Name.TitleSort(),

            Folder = artistFolder,
            HostFolder = Path.Join(ResolveLibraryRoot(), artistFolder).PathName(),
            LibraryId = Library.Id,
            FolderId = Folder!.Id,
        };

        try
        {
            await mediaContext
                .Artists.Upsert(insert)
                .On(e => new { e.Id })
                .WhenMatched(
                    (s, i) =>
                        new()
                        {
                            Id = i.Id,
                            Name = i.Name,
                            TitleSort = i.TitleSort,
                            Disambiguation = i.Disambiguation,
                            Description = i.Description,

                            Folder = i.Folder,
                            HostFolder = i.HostFolder,
                            LibraryId = i.LibraryId,
                            FolderId = i.FolderId,
                        }
                )
                .RunAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e.Message);
            return;
        }

        try
        {
            foreach (MusicBrainzGenreDetails genre in musicBrainzArtist.Genres)
                await LinkGenreToArtist(mediaContext, musicBrainzArtist, genre);
        }
        catch (Exception e)
        {
            _logger.LogError(e.Message);
        }

        MusicMetadataJob musicDescriptionJob = new() { ArtistId = musicBrainzArtist.Id };
        QueueRunner.Current!.Dispatcher.Dispatch(musicDescriptionJob);

        FanArtImagesJob fanartImagesJob = new(musicBrainzArtist);
        QueueRunner.Current!.Dispatcher.Dispatch(fanartImagesJob);

        if (EventBusProvider.IsConfigured)
            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent
                {
                    QueryKey = ["music", "artist", musicBrainzArtist.Id.ToString()],
                }
            );
    }

    private async Task<MusicBrainzTrack?> StoreTrack(
        MediaContext mediaContext,
        MusicBrainzReleaseAppends musicBrainzRelease,
        MusicBrainzTrack musicBrainzTrack,
        MusicBrainzMedia musicBrainzMedia,
        MediaFile mediaFile
    )
    {
        _logger.LogTrace("Processing Track: {Title}", musicBrainzTrack.Title);

        bool hasTrack = mediaContext
            .Tracks.AsNoTracking()
            .Any(t => t.Id == musicBrainzTrack.Id && t.Filename != null && t.Duration != null);

        if (hasTrack)
            return null;

        Track insert = new()
        {
            Id = musicBrainzTrack.Id,
            Name = musicBrainzTrack.Title,
            Date =
                musicBrainzRelease.DateTime
                ?? musicBrainzRelease.ReleaseEvents?.FirstOrDefault()?.DateTime,
            DiscNumber = musicBrainzMedia.Position,
            TrackNumber = musicBrainzTrack.Position,
        };

        string? file = FileMatch(musicBrainzRelease, musicBrainzMedia, insert);

        if (file is not null)
        {
            _logger.LogTrace("File Match: {File}", file);

            if (
                !StoragePathHelpers.TryGetLibraryRelativeParts(
                    file,
                    ResolveLibraryRoot(),
                    out string relativeFolder,
                    out string filename
                )
            )
            {
                _logger.LogWarning(
                    "Skipping track {Title}: '{File}' does not resolve under the library folder",
                    musicBrainzTrack.Title,
                    file
                );
                return null;
            }

            FfProbeData ffProbeData = await FfProbe.CreateAsync(file);

            insert.Filename = filename;
            insert.Quality = (int)Math.Floor(ffProbeData.Format.BitRate / 1000.0);
            insert.Duration = HmsRegex().Replace(ffProbeData.Duration.ToString(@"hh\:mm\:ss"), "");

            string hostFolder = (
                StoragePathHelpers.GetParent(file.Replace('\\', '/')) ?? string.Empty
            ).PathName();

            // A host folder holding two rooted paths is the shape an older
            // import wrote: the album folder once with forward slashes and once
            // with backslashes. Combined with Filename it addresses nothing, so
            // the track is unplayable and its analysis fails on every sweep.
            // The row fails loudly here rather than being guessed at: which half
            // is the real folder is not something the importer can know.
            if (HostFolderPath.TrySplitAtSecondRoot(hostFolder, out string head, out string tail))
            {
                _logger.LogError(
                    "Refusing to store track {Title}: its host folder holds two rooted paths, '{First}' followed by '{Second}'",
                    [musicBrainzTrack.Title, head, tail]
                );
                return null;
            }

            insert.FolderId = Folder!.Id;
            insert.Folder = relativeFolder;
            insert.HostFolder = hostFolder;
        }

        // A track with no file on disk has no folder and no filename, so its
        // composed URL (/{FolderId}{Folder}{Filename}) can never resolve. Only
        // refresh the metadata of a row that already exists; never create one.
        if (file is null && !mediaContext.Tracks.AsNoTracking().Any(t => t.Id == insert.Id))
        {
            _logger.LogTrace("No file matched for track {Title}", musicBrainzTrack.Title);
            return null;
        }

        try
        {
            await mediaContext
                .Tracks.Upsert(insert)
                .On(e => new { e.Id })
                .WhenMatched(
                    (ts, ti) =>
                        new()
                        {
                            Id = ti.Id,
                            Name = ti.Name,
                            DiscNumber = ti.DiscNumber,
                            TrackNumber = ti.TrackNumber,
                            Date = ti.Date,

                            Folder = string.IsNullOrEmpty(file) ? ts.Folder : ti.Folder,
                            FolderId = string.IsNullOrEmpty(file) ? ts.FolderId : ti.FolderId,
                            HostFolder = string.IsNullOrEmpty(file) ? ts.HostFolder : ti.HostFolder,
                            Duration = string.IsNullOrEmpty(file) ? ts.Duration : ti.Duration,
                            Filename = string.IsNullOrEmpty(file) ? ts.Filename : ti.Filename,
                            Quality = string.IsNullOrEmpty(file) ? ts.Quality : ti.Quality,
                        }
                )
                .RunAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e.Message);
            return null;
        }

        try
        {
            foreach (MusicBrainzGenreDetails genre in musicBrainzTrack.Genres ?? [])
                await LinkGenreToTrack(mediaContext, musicBrainzTrack, genre);
        }
        catch (Exception e)
        {
            _logger.LogError(e.Message);
            return null;
        }

        _logger.LogTrace("Track stored: {Title}", musicBrainzTrack.Title);
        return musicBrainzTrack;
    }

    private string? FileMatch(
        MusicBrainzReleaseAppends musicBrainzRelease,
        MusicBrainzMedia musicBrainzMedia,
        Track track
    )
    {
        string? file = FindTrackWithAlbumNumberByNumberPadded(
            musicBrainzMedia,
            null,
            musicBrainzRelease.Media.Length,
            track.TrackNumber,
            4
        );
        file = FindTrackWithAlbumNumberByNumberPadded(
            musicBrainzMedia,
            file,
            musicBrainzRelease.Media.Length,
            track.TrackNumber,
            3
        );
        file = FindTrackWithAlbumNumberByNumberPadded(
            musicBrainzMedia,
            file,
            musicBrainzRelease.Media.Length,
            track.TrackNumber
        );

        file = FindTrackWithoutAlbumNumberByNumberPadded(
            musicBrainzMedia,
            file,
            musicBrainzRelease.Media.Length,
            track.TrackNumber,
            4
        );
        file = FindTrackWithoutAlbumNumberByNumberPadded(
            musicBrainzMedia,
            file,
            musicBrainzRelease.Media.Length,
            track.TrackNumber,
            3
        );
        file = FindTrackWithoutAlbumNumberByNumberPadded(
            musicBrainzMedia,
            file,
            musicBrainzRelease.Media.Length,
            track.TrackNumber
        );

        return file;
    }

    private async Task LinkReleaseToReleaseGroup(
        MediaContext mediaContext,
        MusicBrainzReleaseAppends musicBrainzRelease
    )
    {
        _logger.LogTrace(
            "Linking Release to Release Group: {Title}",
            musicBrainzRelease.MusicBrainzReleaseGroup.Title
        );
        AlbumReleaseGroup insert = new()
        {
            AlbumId = musicBrainzRelease.Id,
            ReleaseGroupId = musicBrainzRelease.MusicBrainzReleaseGroup.Id,
        };

        await mediaContext
            .AlbumReleaseGroup.Upsert(insert)
            .On(e => new { e.AlbumId, e.ReleaseGroupId })
            .WhenMatched((s, i) => new() { AlbumId = i.AlbumId, ReleaseGroupId = i.ReleaseGroupId })
            .RunAsync();
    }

    private async Task LinkArtistToReleaseGroup(
        MediaContext mediaContext,
        MusicBrainzReleaseAppends musicBrainzRelease,
        Guid artistId
    )
    {
        _logger.LogTrace(
            "Linking Artist to Release Group: {Title}",
            musicBrainzRelease.MusicBrainzReleaseGroup.Title
        );
        ArtistReleaseGroup insert = new()
        {
            ArtistId = artistId,
            ReleaseGroupId = musicBrainzRelease.MusicBrainzReleaseGroup.Id,
        };

        await mediaContext
            .ArtistReleaseGroup.Upsert(insert)
            .On(e => new { e.ArtistId, e.ReleaseGroupId })
            .WhenMatched(
                (s, i) => new() { ArtistId = i.ArtistId, ReleaseGroupId = i.ReleaseGroupId }
            )
            .RunAsync();
    }

    private async Task LinkReleaseToLibrary(
        MediaContext mediaContext,
        MusicBrainzReleaseAppends musicBrainzRelease
    )
    {
        _logger.LogTrace("Linking Release to Library: {Title}", musicBrainzRelease.Title);
        AlbumLibrary insert = new() { AlbumId = musicBrainzRelease.Id, LibraryId = Library.Id };

        await mediaContext
            .AlbumLibrary.Upsert(insert)
            .On(e => new { e.AlbumId, e.LibraryId })
            .WhenMatched((s, i) => new() { AlbumId = i.AlbumId, LibraryId = i.LibraryId })
            .RunAsync();
    }

    private async Task LinkArtistToLibrary(
        MediaContext mediaContext,
        MusicBrainzArtist musicBrainzArtistMusicBrainzArtist
    )
    {
        _logger.LogTrace(
            "Linking Artist to Library: {Name}",
            musicBrainzArtistMusicBrainzArtist.Name
        );
        ArtistLibrary insert = new()
        {
            ArtistId = musicBrainzArtistMusicBrainzArtist.Id,
            LibraryId = Library.Id,
        };

        await mediaContext
            .ArtistLibrary.Upsert(insert)
            .On(e => new { e.ArtistId, e.LibraryId })
            .WhenMatched((s, i) => new() { ArtistId = i.ArtistId, LibraryId = i.LibraryId })
            .RunAsync();
    }

    private async Task LinkTrackToRelease(
        MediaContext mediaContext,
        MusicBrainzTrack? track,
        MusicBrainzReleaseAppends? release
    )
    {
        _logger.LogTrace("Linking Track to Release: {Title}", track?.Title);
        if (track == null || release == null)
            return;

        AlbumTrack insert = new() { AlbumId = release.Id, TrackId = track.Id };

        await mediaContext
            .AlbumTrack.Upsert(insert)
            .On(e => new { e.AlbumId, e.TrackId })
            .WhenMatched((s, i) => new() { AlbumId = i.AlbumId, TrackId = i.TrackId })
            .RunAsync();
    }

    private async Task LinkArtistToAlbum(
        MediaContext mediaContext,
        MusicBrainzArtist musicBrainzArtistMusicBrainzArtist,
        MusicBrainzReleaseAppends musicBrainzRelease
    )
    {
        _logger.LogTrace("Linking Artist to Album: {Title}", musicBrainzRelease.Title);
        AlbumArtist insert = new()
        {
            AlbumId = musicBrainzRelease.Id,
            ArtistId = musicBrainzArtistMusicBrainzArtist.Id,
        };

        await mediaContext
            .AlbumArtist.Upsert(insert)
            .On(e => new { e.AlbumId, e.ArtistId })
            .WhenMatched((s, i) => new() { AlbumId = i.AlbumId, ArtistId = i.ArtistId })
            .RunAsync();
    }

    private async Task LinkArtistToTrack(
        MediaContext mediaContext,
        MusicBrainzArtist musicBrainzArtistMusicBrainzArtist,
        MusicBrainzTrack musicBrainzTrack
    )
    {
        _logger.LogTrace("Linking Artist to Track: {Title}", musicBrainzTrack.Title);
        ArtistTrack insert = new()
        {
            ArtistId = musicBrainzArtistMusicBrainzArtist.Id,
            TrackId = musicBrainzTrack.Id,
        };

        await mediaContext
            .ArtistTrack.Upsert(insert)
            .On(e => new { e.ArtistId, e.TrackId })
            .WhenMatched((s, i) => new() { ArtistId = i.ArtistId, TrackId = i.TrackId })
            .RunAsync();
    }

    private async Task LinkGenreToReleaseGroup(
        MediaContext mediaContext,
        MusicBrainzReleaseGroup musicBrainzReleaseGroup,
        MusicBrainzGenreDetails musicBrainzGenre
    )
    {
        _logger.LogTrace("Linking Genre to Release Group: {Title}", musicBrainzReleaseGroup.Title);
        MusicGenreReleaseGroup insert = new()
        {
            GenreId = musicBrainzGenre.Id,
            ReleaseGroupId = musicBrainzReleaseGroup.Id,
        };

        await mediaContext
            .MusicGenreReleaseGroup.Upsert(insert)
            .On(e => new { e.GenreId, e.ReleaseGroupId })
            .WhenMatched((s, i) => new() { GenreId = i.GenreId, ReleaseGroupId = i.ReleaseGroupId })
            .RunAsync();
    }

    private async Task LinkGenreToArtist(
        MediaContext mediaContext,
        MusicBrainzArtistDetails musicBrainzArtist,
        MusicBrainzGenreDetails musicBrainzGenre
    )
    {
        _logger.LogTrace("Linking Genre to Artist: {Name}", musicBrainzArtist.Name);

        bool genreExists = mediaContext
            .MusicGenres.AsNoTracking()
            .Any(g => g.Id == musicBrainzGenre.Id);

        if (!genreExists)
        {
            _logger.LogTrace("Genre does not exist: {Name}, creating it", musicBrainzGenre.Name);
            MusicGenre genreInsert = new()
            {
                Id = musicBrainzGenre.Id,
                Name = musicBrainzGenre.Name,
            };

            await mediaContext
                .MusicGenres.Upsert(genreInsert)
                .On(e => new { e.Id })
                .WhenMatched((s, i) => new() { Id = i.Id, Name = i.Name })
                .RunAsync();
        }

        ArtistMusicGenre insert = new()
        {
            MusicGenreId = musicBrainzGenre.Id,
            ArtistId = musicBrainzArtist.Id,
        };

        await mediaContext
            .ArtistMusicGenre.Upsert(insert)
            .On(e => new { e.MusicGenreId, e.ArtistId })
            .WhenMatched((s, i) => new() { MusicGenreId = i.MusicGenreId, ArtistId = i.ArtistId })
            .RunAsync();
    }

    private async Task LinkGenreToRelease(
        MediaContext mediaContext,
        MusicBrainzReleaseAppends artist,
        MusicBrainzGenreDetails musicBrainzGenre
    )
    {
        _logger.LogTrace("Linking Genre to Album: {Title}", artist.Title);
        AlbumMusicGenre insert = new() { MusicGenreId = musicBrainzGenre.Id, AlbumId = artist.Id };

        await mediaContext
            .AlbumMusicGenre.Upsert(insert)
            .On(e => new { e.MusicGenreId, e.AlbumId })
            .WhenMatched((s, i) => new() { MusicGenreId = i.MusicGenreId, AlbumId = i.AlbumId })
            .RunAsync();
    }

    private async Task LinkGenreToTrack(
        MediaContext mediaContext,
        MusicBrainzTrack musicBrainzTrack,
        MusicBrainzGenreDetails musicBrainzGenre
    )
    {
        _logger.LogTrace("Linking Genre to Track: {Title}", musicBrainzTrack.Title);
        MusicGenreTrack insert = new()
        {
            GenreId = musicBrainzGenre.Id,
            TrackId = musicBrainzTrack.Id,
        };

        await mediaContext
            .MusicGenreTrack.Upsert(insert)
            .On(e => new { e.GenreId, e.TrackId })
            .WhenMatched((s, i) => new() { GenreId = i.GenreId, TrackId = i.TrackId })
            .RunAsync();
    }

    private string? FindTrackWithoutAlbumNumberByNumberPadded(
        MusicBrainzMedia musicBrainzMedia,
        string? file,
        int numberOfAlbums,
        int trackNumber,
        int padding = 2
    )
    {
        if (file is not null)
            return file;
        if (numberOfAlbums > 1)
            return file;

        return Files
            ?.FirstOrDefault(f =>
            {
                string fileName = Path.GetFileName(f.Parsed!.FilePath)
                    .RemoveDiacritics()
                    .RemoveNonAlphaNumericCharacters()
                    .ToLower();

                string matchNumber = $"{trackNumber.ToString().PadLeft(padding, '0')} ";
                string matchString = musicBrainzMedia
                    .Tracks[trackNumber - 1]
                    .Title.RemoveDiacritics()
                    .RemoveNonAlphaNumericCharacters()
                    .ToLower()
                    .Replace(".mp3", "");

                return fileName.StartsWith(matchNumber) && fileName.Contains(matchString);
            })
            ?.Parsed!.FilePath;
    }

    private string? FindTrackWithAlbumNumberByNumberPadded(
        MusicBrainzMedia musicBrainzMedia,
        string? file,
        int numberOfAlbums,
        int trackNumber,
        int padding = 2
    )
    {
        if (file is not null)
            return file;
        if (numberOfAlbums == 1)
            return file;

        return Files
            ?.FirstOrDefault(f =>
            {
                string fileName = Path.GetFileName(f.Parsed!.FilePath)
                    .RemoveDiacritics()
                    .RemoveNonAlphaNumericCharacters()
                    .ToLower();

                string matchNumber =
                    $"{musicBrainzMedia.Position}-{trackNumber.ToString().PadLeft(padding, '0')} ";
                string matchString = musicBrainzMedia
                    .Tracks[trackNumber - 1]
                    .Title.RemoveDiacritics()
                    .RemoveNonAlphaNumericCharacters()
                    .ToLower()
                    .Replace(".mp3", "");

                return fileName.StartsWith(matchNumber) && fileName.Contains(matchString);
            })
            ?.Parsed!.FilePath;
    }

    [GeneratedRegex("^00:")]
    private static partial Regex HmsRegex();

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private string ResolveLibraryRoot()
    {
        if (Folder is null)
            return string.Empty;
        IStorage folderStorage = _storageFactory.For(Folder.Id, Folder.DriverId, string.Empty);
        // Resolve through the driver, not the IStorage facade: the facade's
        // GetFullPath is a LocalStorage-only escape hatch that throws on every
        // remote backend, so a facade call here killed folder-path resolution
        // for NFS / SMB / S3 / WebDAV music libraries.
        return folderStorage.Driver.GetFullPath(Folder.Path);
    }

    [GeneratedRegex(
        @"(?<library_folder>.+?)[\\\/]((?<letter>.{1})?|\[(?<type>.+?)\])[\\\/](?<artist>.+?)?[\\\/]?(\[(?<year>[0-9]{4})\]?\s?(?<album>.*)?)"
    )]
    private static partial Regex PathRegex();
}
