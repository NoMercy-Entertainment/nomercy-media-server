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

using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.FanArt.Client;
using NoMercy.Providers.FanArt.Models;
using Serilog.Events;
using Image = NoMercy.Database.Models.Media.Image;
using Size = System.Drawing.Size;

namespace NoMercy.MediaProcessing.Images;

public class FanArtImageManager(ImageRepository imageRepository) : IFanArtImageManager
{
    private static readonly Size PaletteDecodeSize = new(
        ColorQuantizer.MaxDimension,
        ColorQuantizer.MaxDimension
    );

    public static async Task<string> ColorPalette(string type, Uri url, bool? download = true)
    {
        return await BaseImageManager.ColorPalette(
            FanArtImageClient.Download,
            type,
            url,
            download,
            PaletteDecodeSize
        );
    }

    public async Task<string> MultiColorPalette(
        IEnumerable<BaseImageManager.MultiUriType> items,
        bool? download = true
    )
    {
        return await BaseImageManager.MultiColorPalette(
            FanArtImageClient.Download,
            items,
            download,
            PaletteDecodeSize
        );
    }

    public async Task<ICollection<Image>> StoreArtistImages(
        FanArtArtistDetails fanArtArtistDetails,
        Guid artistId,
        Artist dbArtist
    )
    {
        try
        {
            List<Image> thumbs = fanArtArtistDetails
                .Thumbs.ToList()
                .ConvertAll<Image>(image =>
                    new()
                    {
                        AspectRatio = 1,
                        Type = "thumb",
                        VoteCount = image.Likes,
                        FilePath = "/" + image.Url.FileName(),
                        ArtistId = artistId,
                        Site = image.Url.BasePath(),
                    }
                );
            List<Image> logos = fanArtArtistDetails
                .Logos.ToList()
                .ConvertAll<Image>(image =>
                    new()
                    {
                        AspectRatio = 1,
                        Type = "logo",
                        VoteCount = image.Likes,
                        FilePath = "/" + image.Url.FileName(),
                        ArtistId = artistId,
                        Site = image.Url.BasePath(),
                    }
                );
            List<Image> banners = fanArtArtistDetails
                .Banners.ToList()
                .ConvertAll<Image>(image =>
                    new()
                    {
                        AspectRatio = 1,
                        Type = "banner",
                        VoteCount = image.Likes,
                        FilePath = "/" + image.Url.FileName(),
                        ArtistId = artistId,
                        Site = image.Url.BasePath(),
                    }
                );
            List<Image> hdLogos = fanArtArtistDetails
                .HdLogos.ToList()
                .ConvertAll<Image>(image =>
                    new()
                    {
                        AspectRatio = 1,
                        Type = "hdLogo",
                        VoteCount = image.Likes,
                        FilePath = "/" + image.Url.FileName(),
                        ArtistId = artistId,
                        Site = image.Url.BasePath(),
                    }
                );
            List<Image> artistBackgrounds = fanArtArtistDetails
                .Backgrounds.ToList()
                .ConvertAll<Image>(image =>
                    new()
                    {
                        AspectRatio = 1,
                        Type = "background",
                        VoteCount = image.Likes,
                        FilePath = "/" + image.Url.FileName(),
                        ArtistId = artistId,
                        Site = image.Url.BasePath(),
                    }
                );

            List<Image> images = thumbs
                .Concat(logos)
                .Concat(banners)
                .Concat(hdLogos)
                .Concat(artistBackgrounds)
                .ToList();

            return await imageRepository.StoreArtistImages(images, dbArtist);
        }
        catch (Exception e)
        {
            if (e.Message.Contains("404"))
                return [];
            Logger.FanArt(e.Message, LogEventLevel.Verbose);
        }

        return [];
    }

    public async Task StoreReleaseImages(FanArtAlbum fanArt, Guid releaseId)
    {
        try
        {
            List<Image> covers = [];
            List<Image> cdArts = [];
            foreach ((Guid _, Albums albums) in fanArt.Albums)
            {
                covers.AddRange(
                    albums.Cover.Select(image => new Image
                    {
                        AspectRatio = 1,
                        Type = "cover",
                        VoteCount = image.Likes,
                        FilePath = "/" + image.Url.FileName(),
                        AlbumId = releaseId,
                        Site = image.Url.BasePath(),
                        Name = fanArt.Name,
                    })
                );

                cdArts.AddRange(
                    albums.CdArt.Select(image => new Image
                    {
                        AspectRatio = 1,
                        Type = "cdArt",
                        VoteCount = image.Likes,
                        FilePath = "/" + image.Url.FileName(),
                        AlbumId = releaseId,
                        Site = image.Url.BasePath(),
                        Name = fanArt.Name,
                    })
                );
            }

            ReleaseGroup dbRelease = await imageRepository.GetReleaseImages(releaseId);

            IEnumerable<Image> images = covers
                .Concat(cdArts)
                .Where(image => dbRelease.AlbumReleaseGroup.Any(ar => ar.AlbumId == image.AlbumId));

            Image? albumCover = covers.FirstOrDefault();

            dbRelease.Cover = albumCover?.FilePath ?? dbRelease.Cover;

            foreach (AlbumReleaseGroup albumRelease in dbRelease.AlbumReleaseGroup)
            {
                albumRelease.Album.Cover = albumCover?.FilePath ?? albumRelease.Album.Cover;
            }

            await imageRepository.CommitReleaseChanges();
            await imageRepository.StoreReleaseImages(images);
        }
        catch (Exception e)
        {
            if (e.Message.Contains("404"))
                return;
            Logger.FanArt(e.Message, LogEventLevel.Verbose);
        }
    }

    public async Task<List<Image>> StoreReleaseImages(
        Dictionary<Guid, Albums> fanArtArtistAlbums,
        Guid artistId,
        Artist dbArtist
    )
    {
        List<Album> albums = dbArtist.AlbumArtist.Select(a => a.Album).ToList();

        Dictionary<Guid, Albums> filteredAlbums = fanArtArtistAlbums
            .Where(fa => albums.Any(a => a.Id == fa.Key))
            .ToDictionary();

        List<Image> images = [];
        foreach ((Guid id, Albums fanArtArtistAlbum) in filteredAlbums)
        {
            images.AddRange(await StoreReleaseImages(fanArtArtistAlbum, artistId, dbArtist));
        }

        return images;
    }

    private async Task<ICollection<Image>> StoreReleaseImages(
        Albums fanArtArtistAlbums,
        Guid albumId,
        Artist dbArtist
    )
    {
        try
        {
            List<Image> cdArts = fanArtArtistAlbums
                .CdArt.Select(image => new Image
                {
                    AspectRatio = 1,
                    Type = "cdArt",
                    VoteCount = image.Likes,
                    FilePath = "/" + image.Url.FileName(),
                    AlbumId = albumId,
                    Site = image.Url.BasePath(),
                })
                .ToList();

            List<Image> covers = fanArtArtistAlbums
                .Cover.Select(image => new Image
                {
                    AspectRatio = 1,
                    Type = "cover",
                    VoteCount = image.Likes,
                    FilePath = "/" + image.Url.FileName(),
                    AlbumId = albumId,
                    Site = image.Url.BasePath(),
                })
                .ToList();

            List<Image> images = cdArts.Concat(covers).ToList();

            return await imageRepository.StoreArtistImages(images, dbArtist);
        }
        catch (Exception e)
        {
            if (e.Message.Contains("404"))
                return [];
            Logger.FanArt(e.Message, LogEventLevel.Verbose);
        }

        return [];
    }

    /// <summary>
    /// Fetched the artist's FanArt entry for a cover-palette thumbnail only —
    /// the same response also carries backgrounds, logos, banners and hd logos,
    /// which were read off the wire and discarded. Every artist onboarded
    /// through <c>ArtistManager</c>/<c>RecordingManager</c> (the only two
    /// callers) ended up with a cover thumbnail and nothing else: no backdrop
    /// to show behind "now playing", no logo, no banner — reproduced live
    /// against a FanArt entry (Robbie Williams) that has all four. One fetch
    /// now does both jobs: the palette callers already expect, and persisting
    /// the full set via <see cref="StoreArtistImages"/> so it survives past this
    /// one HTTP response instead of being thrown away.
    /// </summary>
    public static async Task<CoverArtImageManagerManager.CoverPalette?> Add(
        Guid id,
        bool priority = false
    )
    {
        try
        {
            using FanArtMusicClient fanArtMusicClient = new();
            FanArtArtistDetails? fanArt = await fanArtMusicClient.Artist(id);
            if (fanArt is null)
                return null;

            await using MediaContext context = new();
            ImageRepository imageRepository = new(context);
            FanArtImageManager imageManager = new(imageRepository);
            await imageManager.StoreArtistImages(fanArt, id, new Artist { Id = id });

            // A DB row alone 404s at /images/music/{file} until Download() below
            // actually writes the bytes — the thumb loop gets this for free via
            // ColorPalette(); these four types don't have one otherwise.
            foreach (
                Uri url in fanArt
                    .Backgrounds.Concat(fanArt.Logos)
                    .Concat(fanArt.HdLogos)
                    .Concat(fanArt.Banners)
                    .Select(image => image.Url)
            )
                using (await FanArtImageClient.Download(url)) { }

            List<Uri> coverList = fanArt.Thumbs.Select(t => t.Url).ToList();

            foreach (Uri coverItem in coverList)
            {
                if (!coverItem.HasSuccessStatus("image/*"))
                    continue;

                return new() { Palette = await ColorPalette("cover", coverItem), Url = coverItem };
            }

            return null;
        }
        catch (Exception e)
        {
            if (e.Message.Contains("404"))
                return null;
            Logger.FanArt(e.Message, LogEventLevel.Verbose);
            return null;
        }
    }
}
