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
using Microsoft.Extensions.Logging;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Music;
using NoMercy.MediaProcessing.Artists;
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Music;
using NoMercy.MediaProcessing.MusicGenres;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.FanArt.Client;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NoMercy.MediaProcessing.Recordings;

public partial class RecordingManager(
    IRecordingRepository recordingRepository,
    IMusicGenreRepository musicGenreRepository,
    IArtistRepository artistRepository,
    IStorageDriver storageDriver,
    IStorageFactory storageFactory,
    ILogger<RecordingManager> logger
) : BaseManager, IRecordingManager
{
    public async Task<bool> Store(
        MusicBrainzReleaseAppends releaseAppends,
        MusicBrainzTrack musicBrainzTrack,
        MusicBrainzMedia musicBrainzMedia,
        Folder libraryFolder,
        MediaFolder mediaFolder,
        CoverArtImageManagerManager.CoverPalette? releaseCoverPalette
    )
    {
        logger.LogTrace(
            "Storing Recording: {Title} - {Position}-{Position2} {Title2}",
            [
                releaseAppends.Title,
                musicBrainzMedia.Position,
                musicBrainzTrack.Position,
                musicBrainzTrack.Title,
            ]
        );

        MediaScan mediaScan = new(storageDriver);
        ConcurrentBag<MediaFolderExtend> folders = await mediaScan
            .EnableFileListing()
            .FilterByMediaType("music")
            .Process(mediaFolder.Path, 1);

        foreach (MediaFolderExtend folder in folders)
        {
            if (folder.Files is null || folder.Files.IsEmpty)
                continue;
            foreach (MediaFile file in folder.Files)
            {
                MediaFile? mediaFile = FileMatch(
                    file,
                    releaseAppends,
                    musicBrainzMedia,
                    musicBrainzTrack.Position
                );
                if (mediaFile is null)
                    continue;

                TagFile? tagFile = file.TagFile;
                if (tagFile == null || mediaFile.FFprobe == null)
                {
                    logger.LogError("File not found: {Name}", file.Name);
                    continue;
                }

                logger.LogTrace("Recording {Title} found", musicBrainzTrack.Title);

                string scanLibraryRoot = ResolveLibraryRoot(libraryFolder);
                await RelocateToPicardLayout(
                    releaseAppends,
                    musicBrainzTrack,
                    mediaFile,
                    libraryFolder,
                    scanLibraryRoot
                );

                if (
                    !StoragePathHelpers.TryGetLibraryRelativeParts(
                        mediaFile.Path,
                        scanLibraryRoot,
                        out string relativeFolder,
                        out string filename
                    )
                )
                {
                    logger.LogWarning(
                        "Skipping recording {Title}: '{Path}' does not resolve under library folder '{Root}'",
                        [musicBrainzTrack.Title, mediaFile.Path, libraryFolder.Path]
                    );
                    continue;
                }

                string path =
                    StoragePathHelpers.GetParent(mediaFile.Path.Replace('\\', '/')) ?? string.Empty;

                string trackHostFolder = path.PathName();

                // Two rooted paths in one host folder is the shape an older
                // import wrote — the album folder twice, once per separator
                // style. Combined with Filename it addresses nothing, so the
                // row fails loudly rather than being guessed at.
                if (
                    HostFolderPath.TrySplitAtSecondRoot(
                        trackHostFolder,
                        out string head,
                        out string tail
                    )
                )
                {
                    logger.LogError(
                        "Skipping recording {Title}: its host folder holds two rooted paths, '{First}' followed by '{Second}'",
                        [musicBrainzTrack.Title, head, tail]
                    );
                    continue;
                }

                Track insert = new()
                {
                    Id = musicBrainzTrack.Id,
                    Name = musicBrainzTrack.Title,
                    Date =
                        releaseAppends.DateTime
                        ?? releaseAppends.ReleaseEvents?.FirstOrDefault()?.DateTime
                        ?? releaseAppends.MusicBrainzReleaseGroup?.FirstReleaseDate,
                    DiscNumber = musicBrainzMedia.Position,
                    TrackNumber = musicBrainzTrack.Position,

                    Filename = filename,
                    Quality = (int)
                        Math.Floor(
                            (
                                (double?)mediaFile.FFprobe?.Format.BitRate
                                ?? (double?)(tagFile!.Properties?.AudioBitrate * 1000)
                                ?? 0.0
                            ) / 1000.0
                        ),
                    Duration = HmsRegex()
                        .Replace(
                            (
                                mediaFile.FFprobe?.Duration
                                ?? tagFile!.Properties?.Duration
                                ?? TimeSpan.Zero
                            ).ToString(@"hh\:mm\:ss"),
                            ""
                        ),

                    FolderId = libraryFolder.Id,
                    Folder = relativeFolder,
                    HostFolder = trackHostFolder,

                    Cover = releaseCoverPalette?.Url is not null
                        ? $"/{releaseCoverPalette.Url.FileName()}"
                        : null,
                };

                await recordingRepository.Store(insert);

                await LinkToRelease(musicBrainzTrack, releaseAppends);
                await LinkToLibrary(
                    musicBrainzTrack,
                    libraryFolder.FolderLibraries.FirstOrDefault()!.Library
                );

                List<MusicGenreTrack> genres =
                    musicBrainzTrack
                        .Genres?.Select(genre => new MusicGenreTrack
                        {
                            TrackId = musicBrainzTrack.Id,
                            GenreId = genre.Id,
                        })
                        .ToList()
                    ?? [];

                await musicGenreRepository.LinkToRecording(genres);

                new JobDispatcher().DispatchColorPaletteJob("track", insert.Id.ToString());

                logger.LogTrace("Recording {Title} stored", musicBrainzTrack.Title);

                return true;
            }
        }

        return false;
    }

    private async Task LinkToRelease(
        MusicBrainzTrack musicBrainzTrack,
        MusicBrainzReleaseAppends releaseAppends
    )
    {
        logger.LogTrace(
            "Linking Recording to Artist: {Title} - {Title2}",
            [musicBrainzTrack.Title, releaseAppends.MusicBrainzReleaseGroup.Title]
        );

        foreach (ReleaseArtistCredit credit in releaseAppends.ArtistCredit)
        {
            ArtistTrack insert = new()
            {
                ArtistId = credit.MusicBrainzArtist.Id,
                TrackId = musicBrainzTrack.Id,
            };

            await recordingRepository.LinkToArtist(insert);
        }
    }

    private async Task LinkToRelease(Track track, MusicBrainzReleaseAppends releaseAppends)
    {
        logger.LogTrace("Linking Recording to Release: {Title}", releaseAppends.Title);

        AlbumTrack insert = new() { AlbumId = releaseAppends.Id, TrackId = track.Id };

        await recordingRepository.LinkToRelease(insert);
    }

    private async Task LinkToLibrary(MusicBrainzTrack track, Library library)
    {
        logger.LogTrace("Linking Recording to Library: {Title}", track.Title);

        LibraryTrack insert = new() { LibraryId = library.Id, TrackId = track.Id };

        await recordingRepository.LinkToLibrary(insert);
    }

    private async Task LinkToLibrary(Track track, Library library)
    {
        logger.LogTrace("Linking Recording to Library: {Title}", library.Title);

        LibraryTrack insert = new() { LibraryId = library.Id, TrackId = track.Id };

        await recordingRepository.LinkToLibrary(insert);
    }

    private MediaFile? FileMatch(
        MediaFile inputFile,
        MusicBrainzReleaseAppends musicBrainzRelease,
        MusicBrainzMedia musicBrainzMedia,
        int trackNumber
    )
    {
        bool hasMatch = FindTrackWithAlbumNumberByNumberPadded(
            inputFile,
            musicBrainzMedia,
            false,
            musicBrainzRelease.Media.Length,
            trackNumber,
            4
        );
        hasMatch = FindTrackWithAlbumNumberByNumberPadded(
            inputFile,
            musicBrainzMedia,
            hasMatch,
            musicBrainzRelease.Media.Length,
            trackNumber,
            3
        );
        hasMatch = FindTrackWithAlbumNumberByNumberPadded(
            inputFile,
            musicBrainzMedia,
            hasMatch,
            musicBrainzRelease.Media.Length,
            trackNumber
        );

        hasMatch = FindTrackWithoutAlbumNumberByNumberPadded(
            inputFile,
            musicBrainzMedia,
            hasMatch,
            musicBrainzRelease.Media.Length,
            trackNumber,
            4
        );
        hasMatch = FindTrackWithoutAlbumNumberByNumberPadded(
            inputFile,
            musicBrainzMedia,
            hasMatch,
            musicBrainzRelease.Media.Length,
            trackNumber,
            3
        );
        hasMatch = FindTrackWithoutAlbumNumberByNumberPadded(
            inputFile,
            musicBrainzMedia,
            hasMatch,
            musicBrainzRelease.Media.Length,
            trackNumber
        );

        if (!hasMatch)
            return null;
        return inputFile;
    }

    private bool FindTrackWithoutAlbumNumberByNumberPadded(
        MediaFile inputFile,
        MusicBrainzMedia musicBrainzMedia,
        bool hasMatch,
        int numberOfAlbums,
        int trackNumber,
        int padding = 2
    )
    {
        if (hasMatch)
            return true;
        if (numberOfAlbums > 1)
            return false;
        if (inputFile.Parsed is null)
            return false;

        string fileName = Path.GetFileName(inputFile.Parsed.FilePath)
            .RemoveDiacritics()
            .RemoveNonAlphaNumericCharacters()
            .ToLower();

        string matchNumber = $"{trackNumber.ToString().PadLeft(padding, '0')} ";
        if (musicBrainzMedia.Tracks.Length < trackNumber)
            return false;
        string matchString = musicBrainzMedia
            .Tracks[trackNumber - 1]
            .Title.RemoveDiacritics()
            .RemoveNonAlphaNumericCharacters()
            .ToLower()
            .Replace(".mp3", "");

        return fileName.StartsWith(matchNumber) && fileName.Contains(matchString);
    }

    private bool FindTrackWithAlbumNumberByNumberPadded(
        MediaFile inputFile,
        MusicBrainzMedia musicBrainzMedia,
        bool hasMatch,
        int numberOfAlbums,
        int trackNumber,
        int padding = 2
    )
    {
        if (hasMatch)
            return true;
        if (numberOfAlbums == 1)
            return false;
        if (inputFile.Parsed is null)
            return false;

        string fileName = Path.GetFileName(inputFile.Parsed.FilePath)
            .RemoveDiacritics()
            .RemoveNonAlphaNumericCharacters()
            .ToLower();

        string matchNumber =
            $"{musicBrainzMedia.Position}-{trackNumber.ToString().PadLeft(padding, '0')} ";
        if (musicBrainzMedia.Tracks.Length < trackNumber)
            return false;
        string matchString = musicBrainzMedia
            .Tracks[trackNumber - 1]
            .Title.RemoveDiacritics()
            .RemoveNonAlphaNumericCharacters()
            .ToLower()
            .Replace(".mp3", "");

        return fileName.StartsWith(matchNumber) && fileName.Contains(matchString);
    }

    private string ResolveLibraryRoot(Folder libraryFolder)
    {
        IStorage folderStorage = storageFactory.For(
            libraryFolder.Id,
            libraryFolder.DriverId,
            string.Empty
        );
        return FolderRootPath(folderStorage, libraryFolder.Path);
    }

    [GeneratedRegex("^00:")]
    private static partial Regex HmsRegex();

    /// <summary>
    /// Places the source file itself on Stoney's Picard layout, not just an encoder's
    /// output copy. <see cref="MusicEncodeDispatcher"/> only ever organizes an ENCODED
    /// file — a library with encoding turned off left every imported file sitting
    /// wherever it was scanned from, undoing the whole point of importing it. Reuses the
    /// same <see cref="PicardNaming"/> rules and the same <see cref="MusicEncodeDispatcher.NamingContextFor"/>
    /// so a file lands in the identical folder an encode of it would have used.
    /// A no-op when the file already sits at its target path.
    /// </summary>
    private async Task RelocateToPicardLayout(
        MusicBrainzReleaseAppends releaseAppends,
        MusicBrainzTrack trackAppends,
        MediaFile mediaFile,
        Folder libraryFolder,
        string libraryRoot
    )
    {
        if (
            !StoragePathHelpers.TryGetLibraryRelativeParts(
                mediaFile.Path,
                libraryRoot,
                out string currentRelativeFolder,
                out string currentFilename
            )
        )
            return;

        // TryGetLibraryRelativeParts returns currentFilename with its OWN leading slash
        // (the same shape Track.Filename is stored in, e.g. "/01 Nebraska.mp3") — trimming
        // only the folder side still leaves a doubled separator at the join.
        string currentRelativePath =
            $"{currentRelativeFolder.Trim('/')}/{currentFilename.TrimStart('/')}".Trim('/');

        MusicNamingContext namingContext = MusicEncodeDispatcher.NamingContextFor(
            releaseAppends,
            trackAppends
        );
        string targetRelativePath = PicardNaming.Sanitize(
            $"{PicardNaming.BuildDirectory(namingContext)}/{PicardNaming.BuildFileName(namingContext)}{Path.GetExtension(mediaFile.Path)}"
        );

        if (string.Equals(currentRelativePath, targetRelativePath, StringComparison.Ordinal))
            return;

        IStorage libraryStorage = storageFactory.For(
            libraryFolder.Id,
            libraryFolder.DriverId,
            string.Empty
        );

        // storageFactory.For(...) scopes to the DRIVE root, not the library folder — the
        // same reason ResolveLibraryRoot resolves libraryFolder.Path through it rather than
        // treating the folder itself as the scope. MoveAsync validates its paths against
        // that same drive-rooted scope, so both sides need the folder's own path prefixed
        // back on, or a move lands one directory short of where relativeFolder says it did.
        string scopedFrom = $"{libraryFolder.Path.Trim('/')}/{currentRelativePath}".Trim('/');
        string scopedTo = $"{libraryFolder.Path.Trim('/')}/{targetRelativePath}".Trim('/');

        // A repair can be untangling a genuine swap — track A's real file already sits
        // where track B's file needs to land, and vice versa. MoveFile refuses to
        // overwrite, so a straight rename into an occupied slot throws. Side-stepping the
        // occupant into a disambiguated name in the same folder — rather than overwriting
        // it — keeps its bytes intact for the NEXT repair pass to re-fingerprint and place
        // correctly, once the slot it actually needs has, in turn, been vacated.
        if (await libraryStorage.ExistsAsync(scopedTo, CancellationToken.None))
        {
            string displaced =
                $"{StoragePathHelpers.GetParent(scopedTo)}/.repair-displaced-{Guid.NewGuid():N}{Path.GetExtension(scopedTo)}";
            await libraryStorage.MoveAsync(scopedTo, displaced, CancellationToken.None);
            logger.LogInformation(
                "Displaced occupant of '{Path}' to '{Displaced}' to make room for {Track}",
                scopedTo,
                displaced,
                trackAppends.Title
            );
        }

        await libraryStorage.MoveAsync(scopedFrom, scopedTo, CancellationToken.None);

        logger.LogInformation(
            "Organized {Track}: moved '{From}' to '{To}'",
            trackAppends.Title,
            currentRelativePath,
            targetRelativePath
        );

        mediaFile.Path = $"{libraryRoot.TrimEnd('/')}/{targetRelativePath}";
    }

    public async Task Store(
        MusicBrainzReleaseAppends releaseAppends,
        MusicBrainzTrack trackAppends,
        MusicBrainzArtistAppends[] artistAppends,
        MediaFile mediaFile,
        Folder libraryFolder,
        CoverArtImageManagerManager.CoverPalette? releaseCoverPalette
    )
    {
        JobDispatcher jobDispatcher = new();
        logger.LogTrace("Recording {Title} found", releaseAppends.Title);

        string libraryRoot = ResolveLibraryRoot(libraryFolder);
        await RelocateToPicardLayout(
            releaseAppends,
            trackAppends,
            mediaFile,
            libraryFolder,
            libraryRoot
        );

        StoragePathHelpers.TryGetLibraryRelativeParts(
            mediaFile.Path,
            libraryRoot,
            out string artistRelativeFolder,
            out _
        );

        foreach (MusicBrainzArtistAppends artist in artistAppends)
        {
            try
            {
                CoverArtImageManagerManager.CoverPalette? coverPalette =
                    await FanArtImageManager.Add(artist.Id, true);

                if (coverPalette is not null)
                {
                    using Image<Rgba32>? downloadedImage = await FanArtImageClient.Download(
                        coverPalette.Url!
                    );
                }

                Artist artistEntity = new()
                {
                    Id = artist.Id,
                    Name = artist.Name,
                    Disambiguation = artist.Disambiguation,
                    Cover = coverPalette?.Url is not null
                        ? $"/{coverPalette.Url.FileName()}"
                        : null,
                    TitleSort = artist.SortName,
                    Country = artist.Country,
                    Year = artist.LifeSpan?.BeginDate?.Year,

                    FolderId = libraryFolder.Id,
                    Folder = artistRelativeFolder,
                    HostFolder = (
                        StoragePathHelpers.GetParent(mediaFile.Path.Replace('\\', '/'))
                        ?? string.Empty
                    ).PathName(),

                    LibraryId = libraryFolder.FolderLibraries.FirstOrDefault()!.LibraryId,
                };

                await artistRepository.StoreAsync(artistEntity);
                jobDispatcher.DispatchJob<MusicMetadataJob>(artist);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to store recording artist metadata");
            }
        }

        if (
            !StoragePathHelpers.TryGetLibraryRelativeParts(
                mediaFile.Path,
                libraryRoot,
                out string relativeFolder,
                out string filename
            )
        )
        {
            logger.LogWarning(
                "Skipping recording {Title}: '{Path}' does not resolve under library folder '{Root}'",
                [trackAppends.Title, mediaFile.Path, libraryFolder.Path]
            );
            return;
        }

        string path =
            StoragePathHelpers.GetParent(mediaFile.Path.Replace('\\', '/')) ?? string.Empty;

        string trackHostFolder = path.PathName();

        // See the same guard on the release path above: a host folder holding
        // two rooted paths addresses no file at all, so the row fails loudly
        // instead of being stored with a value nothing can resolve.
        if (HostFolderPath.TrySplitAtSecondRoot(trackHostFolder, out string head, out string tail))
        {
            logger.LogError(
                "Skipping recording {Title}: its host folder holds two rooted paths, '{First}' followed by '{Second}'",
                [trackAppends.Title, head, tail]
            );
            return;
        }

        Track insert = new()
        {
            Id = trackAppends.Id,
            Name = trackAppends.Title,
            Date =
                trackAppends.Recording.FirstReleaseDate
                ?? releaseAppends.DateTime
                ?? releaseAppends.ReleaseEvents?.FirstOrDefault()?.DateTime
                ?? releaseAppends.MusicBrainzReleaseGroup?.FirstReleaseDate,
            DiscNumber = mediaFile.Parsed?.DiscNumber ?? 0,
            TrackNumber = mediaFile.Parsed?.TrackNumber ?? 0,

            Filename = filename,
            Quality = (int)
                Math.Floor(
                    (
                        mediaFile.FFprobe?.Format.BitRate
                        ?? mediaFile.TagFile?.Properties?.AudioBitrate * 1000
                        ?? 0
                    ) / 1000.0
                ),
            Duration = HmsRegex()
                .Replace(
                    (
                        mediaFile.FFprobe?.Duration ?? mediaFile.TagFile!.Properties!.Duration
                    ).ToString(@"hh\:mm\:ss"),
                    ""
                ),

            FolderId = libraryFolder.Id,
            Folder = relativeFolder,
            HostFolder = trackHostFolder,

            Cover = releaseCoverPalette?.Url is not null
                ? $"/{releaseCoverPalette.Url.FileName()}"
                : null,
        };

        await recordingRepository.Store(insert);

        await LinkToRelease(insert, releaseAppends);
        await LinkToLibrary(insert, libraryFolder.FolderLibraries.FirstOrDefault()!.Library);
        await LinkToArtist(insert, artistAppends);
        foreach (
            MusicBrainzGenreDetails musicBrainzGenreDetails in trackAppends.Genres
                ?? trackAppends.Recording.Genres
        )
        {
            MusicGenre musicGenre = new()
            {
                Id = musicBrainzGenreDetails.Id,
                Name = musicBrainzGenreDetails.Name,
            };
            await musicGenreRepository.Store(musicGenre);
        }
        List<MusicGenreTrack> genres =
        [
            .. (trackAppends.Genres ?? trackAppends.Recording.Genres).Select(
                genre => new MusicGenreTrack { TrackId = insert.Id, GenreId = genre.Id }
            ),
        ];

        if (genres.Count > 0)
            await musicGenreRepository.LinkToRecording(genres);

        logger.LogTrace("Recording {Title} stored", trackAppends.Title);
    }

    private async Task LinkToArtist(Track insert, MusicBrainzArtistAppends[] artistAppends)
    {
        foreach (MusicBrainzArtistAppends artist in artistAppends)
        {
            ArtistTrack link = new() { ArtistId = artist.Id, TrackId = insert.Id };
            await recordingRepository.LinkToArtist(link);
        }
    }
}
