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

using Microsoft.Extensions.Logging;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.MusicGenres;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.FanArt.Client;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Storage;
using ImageMagick;

namespace NoMercy.MediaProcessing.Artists;

public class ArtistManager(
    IArtistRepository artistRepository,
    IMusicGenreRepository musicGenreRepository,
    JobDispatcher jobDispatcher,
    IStorageFactory storageFactory,
    ILogger<ArtistManager> logger
) : BaseManager, IArtistManager
{
    /** this is the store for a Release artist */
    public async Task Store(
        ReleaseArtistCredit artistCredit,
        Library library,
        Folder libraryFolder,
        MediaFolder mediaFolder,
        MusicBrainzReleaseAppends releaseAppends
    )
    {
        logger.LogTrace("Storing Artist: {Name}", artistCredit.MusicBrainzArtist.Name);
        string artistFolder = MakeArtistFolder(artistCredit.MusicBrainzArtist.Name);
        string folder = mediaFolder.Path.Replace(ResolveLibraryRoot(libraryFolder), "");

        Artist artist = new()
        {
            Id = artistCredit.MusicBrainzArtist.Id,
            Name = artistCredit.MusicBrainzArtist.Name,
            Disambiguation = string.IsNullOrEmpty(artistCredit.MusicBrainzArtist.Disambiguation)
                ? null
                : artistCredit.MusicBrainzArtist.Disambiguation,
            Country = artistCredit.MusicBrainzArtist.Country,
            TitleSort = string.IsNullOrEmpty(artistCredit.MusicBrainzArtist.SortName)
                ? artistCredit.MusicBrainzArtist.Name.TitleSort()
                : artistCredit.MusicBrainzArtist.SortName,

            LibraryId = library.Id,
            FolderId = libraryFolder.Id,

            Folder = artistFolder,
            HostFolder = folder.PathName(),
        };

        await artistRepository.StoreAsync(artist);
        jobDispatcher.DispatchColorPaletteJob("artist", artist.Id.ToString());

        await LinkToLibrary(artistCredit.MusicBrainzArtist, library);
        await LinkToRelease(artistCredit.MusicBrainzArtist, releaseAppends);

        try
        {
            List<ArtistMusicGenre> genres = artistCredit
                .MusicBrainzArtist.Genres.Select(genre => new ArtistMusicGenre
                {
                    ArtistId = artistCredit.MusicBrainzArtist.Id,
                    MusicGenreId = genre.Id,
                })
                .ToList();

            await musicGenreRepository.LinkToArtist(genres);
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
        }

        if (EventBusProvider.IsConfigured)
            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent
                {
                    QueryKey = ["music", "artist", artistCredit.MusicBrainzArtist.Id.ToString()],
                }
            );
    }

    /** this is the store for a Release artist */
    public async Task Store(
        MusicBrainzArtistAppends artistCredit,
        MusicBrainzReleaseAppends releaseAppends,
        Library library,
        Folder libraryFolder
    )
    {
        logger.LogTrace("Storing Artist: {Name}", artistCredit.Name);
        string artistFolder = MakeArtistFolder(artistCredit.Name);
        string folder = artistFolder.Replace("/", StringExtensions.DirectorySeparator);

        CoverArtImageManagerManager.CoverPalette? coverPalette = await GetCoverArtForArtist(
            artistCredit
        );

        Artist artist = new()
        {
            Id = artistCredit.Id,
            Name = artistCredit.Name,
            Disambiguation = string.IsNullOrEmpty(artistCredit.Disambiguation)
                ? null
                : artistCredit.Disambiguation,
            Cover = coverPalette?.Url is not null ? $"/{coverPalette.Url.FileName()}" : null,
            Country = artistCredit.Country,
            TitleSort = string.IsNullOrEmpty(artistCredit.SortName)
                ? artistCredit.Name.TitleSort()
                : artistCredit.SortName,

            LibraryId = library.Id,
            FolderId = libraryFolder.Id,

            Folder = artistFolder,
            HostFolder = folder.PathName(),
        };

        await artistRepository.StoreAsync(artist);
        jobDispatcher.DispatchColorPaletteJob("artist", artist.Id.ToString());
        jobDispatcher.DispatchJob<MusicMetadataJob>(artistCredit);

        await LinkToLibrary(artistCredit, library);
        await LinkToRelease(artistCredit, releaseAppends);

        foreach (MusicBrainzGenreDetails musicBrainzGenreDetails in artistCredit.Genres)
        {
            MusicGenre musicGenre = new()
            {
                Id = musicBrainzGenreDetails.Id,
                Name = musicBrainzGenreDetails.Name,
            };
            await musicGenreRepository.Store(musicGenre);
        }
        try
        {
            List<ArtistMusicGenre> genres = artistCredit
                .Genres.Select(genre => new ArtistMusicGenre
                {
                    ArtistId = artistCredit.Id,
                    MusicGenreId = genre.Id,
                })
                .ToList();

            await musicGenreRepository.LinkToArtist(genres);
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
        }

        if (EventBusProvider.IsConfigured)
            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent
                {
                    QueryKey = ["music", "artist", artistCredit.Id.ToString()],
                }
            );
    }

    /** this is the store for a Recording artist */
    public async Task Store(
        MusicBrainzArtistDetails artistCredit,
        Library library,
        Folder libraryFolder,
        MediaFolder mediaFolder,
        MusicBrainzTrack track
    )
    {
        logger.LogTrace("Storing Artist: {Name}", artistCredit.Name);
        string artistFolder = MakeArtistFolder(artistCredit.Name);
        string folder = mediaFolder.Path.Replace(ResolveLibraryRoot(libraryFolder), "");

        CoverArtImageManagerManager.CoverPalette? coverPalette = await GetCoverArtForArtist(
            artistCredit
        );

        Artist artist = new()
        {
            Id = artistCredit.Id,
            Name = artistCredit.Name,
            Disambiguation = string.IsNullOrEmpty(artistCredit.Disambiguation)
                ? null
                : artistCredit.Disambiguation,
            Cover = coverPalette?.Url is not null ? $"/{coverPalette.Url.FileName()}" : null,
            Country = artistCredit.Country,
            TitleSort = string.IsNullOrEmpty(artistCredit.SortName)
                ? artistCredit.Name.TitleSort()
                : artistCredit.SortName,

            LibraryId = library.Id,
            FolderId = libraryFolder.Id,

            Folder = artistFolder,
            HostFolder = folder.PathName(),
        };

        await artistRepository.StoreAsync(artist);
        jobDispatcher.DispatchColorPaletteJob("artist", artist.Id.ToString());
        jobDispatcher.DispatchJob<MusicMetadataJob>(artistCredit);

        await LinkToLibrary(artistCredit, library);
        await LinkToTrack(artistCredit, track);
        foreach (MusicBrainzGenreDetails musicBrainzGenreDetails in artistCredit.Genres)
        {
            MusicGenre musicGenre = new()
            {
                Id = musicBrainzGenreDetails.Id,
                Name = musicBrainzGenreDetails.Name,
            };
            await musicGenreRepository.Store(musicGenre);
        }
        try
        {
            List<ArtistMusicGenre> genres = artistCredit
                .Genres.Select(genre => new ArtistMusicGenre
                {
                    ArtistId = artistCredit.Id,
                    MusicGenreId = genre.Id,
                })
                .ToList();

            await musicGenreRepository.LinkToArtist(genres);
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
        }

        if (EventBusProvider.IsConfigured)
            await EventBusProvider.Current.PublishAsync(
                new LibraryRefreshedEvent
                {
                    QueryKey = ["music", "artist", artistCredit.Id.ToString()],
                }
            );
    }

    private static async Task<CoverArtImageManagerManager.CoverPalette?> GetCoverArtForArtist(
        MusicBrainzArtistDetails artistCredit
    )
    {
        CoverArtImageManagerManager.CoverPalette? coverPalette = await FanArtImageManager.Add(
            artistCredit.Id,
            true
        );

        if (coverPalette is not null)
        {
            using MagickImage? downloadedImage = await FanArtImageClient.Download(
                coverPalette.Url!
            );
        }

        return coverPalette;
    }

    private async Task LinkToTrack(MusicBrainzArtistDetails artistCredit, MusicBrainzTrack track)
    {
        logger.LogTrace("Linking Artist to Track: {Name}", artistCredit.Name);

        ArtistTrack insert = new() { ArtistId = artistCredit.Id, TrackId = track.Id };

        await artistRepository.LinkToRecording(insert);
    }

    private async Task LinkToRelease(
        MusicBrainzArtistDetails artistMusicBrainzArtist,
        MusicBrainzReleaseAppends releaseAppends
    )
    {
        logger.LogTrace("Linking Artist to Release: {Name}", artistMusicBrainzArtist.Name);

        AlbumArtist insert = new()
        {
            ArtistId = artistMusicBrainzArtist.Id,
            AlbumId = releaseAppends.Id,
        };

        await artistRepository.LinkToRelease(insert);
    }

    private async Task LinkToLibrary(
        MusicBrainzArtistDetails artistMusicBrainzArtist,
        Library library
    )
    {
        logger.LogTrace("Linking Artist to Library: {Name}", artistMusicBrainzArtist.Name);

        ArtistLibrary insert = new()
        {
            ArtistId = artistMusicBrainzArtist.Id,
            LibraryId = library.Id,
        };

        await artistRepository.LinkToLibrary(insert);
    }

    private static string MakeArtistFolder(string artist)
    {
        string artistName = artist.RemoveDiacritics();

        string artistFolder = char.IsNumber(artistName[0])
            ? "#"
            : artistName[0].ToString().ToUpper();

        return $"/{artistFolder}/{artistName}";
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
}
