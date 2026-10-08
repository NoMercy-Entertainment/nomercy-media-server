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
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.MusicGenres;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.CoverArt.Client;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercy.Storage;
using ImageMagick;

namespace NoMercy.MediaProcessing.Releases;

public class ReleaseManager(
    IReleaseRepository releaseRepository,
    IMusicGenreRepository musicGenreRepository,
    IStorageFactory storageFactory,
    JobDispatcher jobDispatcher,
    ILogger<ReleaseManager> logger
) : BaseManager, IReleaseManager
{
    public async Task<(
        MusicBrainzReleaseAppends? releaseAppends,
        CoverArtImageManagerManager.CoverPalette? coverPalette
    )> Add(Guid id, Library albumLibrary, Folder libraryFolder, MediaFolder mediaFolder)
    {
        logger.LogTrace("Adding Release: {Id} to Library: {Title}", [id, albumLibrary.Title]);

        MusicBrainzReleaseClient musicBrainzReleaseClient = new();
        MusicBrainzReleaseAppends? releaseAppends = await musicBrainzReleaseClient.WithAllAppends(
            id
        );

        if (releaseAppends == null)
            return (null, null);

        CoverArtImageManagerManager.CoverPalette? coverPalette =
            await CoverArtImageManagerManager.Add(releaseAppends.MusicBrainzReleaseGroup.Id);

        if (coverPalette is not null)
        {
            using MagickImage? downloadedImage = await CoverArtCoverArtClient.Download(
                coverPalette.Url
            );
        }

        await Store(releaseAppends, albumLibrary, libraryFolder, mediaFolder, coverPalette);

        return (releaseAppends, coverPalette);
    }

    private async Task Store(
        MusicBrainzReleaseAppends releaseAppends,
        Library library,
        Folder libraryFolder,
        MediaFolder mediaFolder,
        CoverArtImageManagerManager.CoverPalette? coverPalette
    )
    {
        try
        {
            logger.LogTrace("Storing Release: {Title}", releaseAppends.Title);

            string libraryRoot = ResolveLibraryRoot(libraryFolder);
            string folder = mediaFolder.Path.Replace(libraryRoot, "");

            Album release = new()
            {
                Id = releaseAppends.Id,
                Name = releaseAppends.Title,
                Country = releaseAppends.Country,
                Disambiguation = string.IsNullOrEmpty(releaseAppends.Disambiguation)
                    ? null
                    : releaseAppends.Disambiguation,
                Year = releaseAppends.ResolvedYear() ?? 0,
                Tracks = releaseAppends.Media.Sum(m => m.TrackCount),

                LibraryId = library.Id,
                FolderId = libraryFolder.Id,
                HostFolder = folder.PathName(),

                Folder = folder.Replace("\\", "/"),

                Cover = coverPalette?.Url is not null ? $"/{coverPalette.Url.FileName()}" : null,

                _colorPalette = (coverPalette?.Palette).OrEmpty(),
            };

            await releaseRepository.Store(release);
            jobDispatcher.DispatchColorPaletteJob("album", release.Id.ToString());

            await LinkToLibrary(releaseAppends, library);

            List<AlbumMusicGenre> genres = releaseAppends
                .Genres.Select(genre => new AlbumMusicGenre
                {
                    AlbumId = releaseAppends.Id,
                    MusicGenreId = genre.Id,
                })
                .ToList();

            await musicGenreRepository.LinkToRelease(genres);

            logger.LogTrace("Release {Title} stored", releaseAppends.Title);
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
        }
    }

    private async Task LinkToLibrary(MusicBrainzReleaseAppends releaseAppends, Library library)
    {
        logger.LogTrace("Linking Release to Library: {Title}", releaseAppends.Title);

        AlbumLibrary insert = new() { AlbumId = releaseAppends.Id, LibraryId = library.Id };

        await releaseRepository.LinkToLibrary(insert);
    }

    public async Task Store(
        MusicBrainzReleaseAppends releaseAppends,
        Library library,
        Folder libraryFolder,
        MediaFile mediaFile,
        CoverArtImageManagerManager.CoverPalette? coverPalette
    )
    {
        try
        {
            logger.LogTrace("Storing Release: {Title}", releaseAppends.Title);

            string libraryRoot = ResolveLibraryRoot(libraryFolder);
            string folder = StoragePathHelpers
                .GetParent(mediaFile.Path.Replace(libraryRoot, ""))
                .OrEmpty();

            Album release = new()
            {
                Id = releaseAppends.Id,
                Name = releaseAppends.Title,
                Country = releaseAppends.Country,
                Disambiguation = string.IsNullOrEmpty(releaseAppends.Disambiguation)
                    ? null
                    : releaseAppends.Disambiguation,
                Year = releaseAppends.ResolvedYear() ?? 0,
                Tracks = releaseAppends.Media.Sum(m => m.TrackCount),

                LibraryId = library.Id,
                FolderId = libraryFolder.Id,
                HostFolder = folder.PathName(),

                Folder = folder.Replace("\\", "/"),

                Cover = coverPalette?.Url is not null ? $"/{coverPalette.Url.FileName()}" : null,

                _colorPalette = (coverPalette?.Palette).OrEmpty(),
            };

            await releaseRepository.Store(release);
            jobDispatcher.DispatchColorPaletteJob("album", release.Id.ToString());

            await LinkToLibrary(releaseAppends, library);
            await LinkToReleaseGroup(releaseAppends);
            await LinkToGenre(releaseAppends);

            logger.LogTrace("Release {Title} stored", releaseAppends.Title);
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
        }
    }

    private async Task LinkToGenre(MusicBrainzReleaseAppends releaseAppends)
    {
        List<AlbumMusicGenre> genres = releaseAppends
            .Genres.Select(genre => new AlbumMusicGenre
            {
                AlbumId = releaseAppends.Id,
                MusicGenreId = genre.Id,
            })
            .ToList();

        await musicGenreRepository.LinkToRelease(genres);
    }

    private async Task LinkToReleaseGroup(MusicBrainzReleaseAppends releaseAppends)
    {
        logger.LogTrace("Linking Release to Release Group: {Title}", releaseAppends.Title);

        AlbumReleaseGroup insert = new()
        {
            AlbumId = releaseAppends.Id,
            ReleaseGroupId = releaseAppends.MusicBrainzReleaseGroup.Id,
        };

        await releaseRepository.LinkToReleaseGroup(insert);
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
