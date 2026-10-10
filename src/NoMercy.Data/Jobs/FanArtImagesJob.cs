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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.Images;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.FanArt.Client;
using NoMercy.Providers.FanArt.Models;
using NoMercy.Providers.MusicBrainz.Models;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using Image = NoMercy.Database.Models.Media.Image;

namespace NoMercy.Data.Jobs;

[Serializable]
public class FanArtImagesJob : IShouldQueue, IJobStorageInjector
{
    [JsonIgnore]
    public ILoggerFactory LoggerFactory { get; set; } = null!;

    [JsonIgnore]
    private ILogger Log => field ??= LoggerFactory.CreateLogger(GetType());

    public void InjectStorageServices(IServiceProvider serviceProvider)
    {
        LoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
    }

    [JsonIgnore]
    public MusicCoverFiles CoverFiles { get; set; } = new();

    public string QueueName => "image";
    public int Priority => 2;

    /// <summary>
    /// The artist this job fetches art for, or <see cref="Guid.Empty"/> when it
    /// fetches art for a release group instead.
    /// <para>
    /// Ids rather than the graphs: a MusicBrainz artist and release were being
    /// serialized into the payload whole, and the only things ever read back out
    /// were these two ids.
    /// </para>
    /// </summary>
    public Guid ArtistId { get; set; }

    /// <summary>
    /// The release GROUP, which is what FanArt keys albums by — not the release.
    /// </summary>
    public Guid ReleaseGroupId { get; set; }

    // Read from a payload but never written back to one, so a job queued before
    // the ids replaced the graphs still knows what it describes.
    public MusicBrainzArtist? MusicBrainzArtist { get; set; }

    public bool ShouldSerializeMusicBrainzArtist() => false;

    public MusicBrainzReleaseAppends? MusicBrainzRelease { get; set; }

    public bool ShouldSerializeMusicBrainzRelease() => false;

    // Constructor injection: the queue worker builds the job via
    // ActivatorUtilities, so the logger factory arrives without the
    // post-construction InjectStorageServices hook. The parameterless
    // ctor below is kept for deserialization and direct construction.
    [ActivatorUtilitiesConstructor]
    public FanArtImagesJob(ILoggerFactory loggerFactory)
    {
        LoggerFactory = loggerFactory;
    }

    public FanArtImagesJob()
    {
        //
    }

    public FanArtImagesJob(MusicBrainzArtist musicBrainzArtist)
    {
        ArtistId = musicBrainzArtist.Id;
    }

    public FanArtImagesJob(MusicBrainzReleaseAppends musicBrainzRelease)
    {
        ReleaseGroupId = musicBrainzRelease.MusicBrainzReleaseGroup.Id;
    }

    public async Task Handle()
    {
        if (ArtistId == Guid.Empty && MusicBrainzArtist is not null)
            ArtistId = MusicBrainzArtist.Id;

        if (ReleaseGroupId == Guid.Empty && MusicBrainzRelease is not null)
            ReleaseGroupId = MusicBrainzRelease.MusicBrainzReleaseGroup.Id;

        if (ArtistId != Guid.Empty)
            await StoreArtist(ArtistId);

        if (ReleaseGroupId != Guid.Empty)
            await StoreRelease(ReleaseGroupId);
    }

    public async Task StoreArtist(Guid artistId)
    {
        try
        {
            using FanArtMusicClient fanArtMusicClient = new();
            FanArtArtistDetails? fanArt = await fanArtMusicClient.Artist(artistId);
            if (fanArt is null)
                return;

            await using MediaContext mediaContext = new();
            await StoreArtistImages(mediaContext, fanArt, artistId);
        }
        catch (Exception e)
        {
            if (e.Message.Contains("404"))
                return;
            Log.LogTrace(e.Message);
        }
    }

    public async Task StoreArtistImages(
        MediaContext mediaContext,
        FanArtArtistDetails fanArt,
        Guid artistId
    )
    {
        List<Image> thumbs = fanArt
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

        List<Image> logos = fanArt
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
        List<Image> banners = fanArt
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
        List<Image> hdLogos = fanArt
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
        List<Image> artistBackgrounds = fanArt
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

        Artist dbArtist = await mediaContext.Artists.FirstAsync(a => a.Id == artistId);

        if (dbArtist.Cover is null || !await CoverFiles.IsStoredAsync(dbArtist.Cover))
            dbArtist.Cover =
                await CoverFiles.StoreFirstFanArtAsync(fanArt.Thumbs.Select(t => t.Url))
                ?? dbArtist.Cover;

        await mediaContext.SaveChangesAsync();

        await mediaContext
            .Images.UpsertRange(images)
            .On(v => new { v.FilePath, v.ArtistId })
            .WhenMatched(
                (s, i) =>
                    new()
                    {
                        AspectRatio = i.AspectRatio,
                        Height = i.Height,
                        FilePath = i.FilePath,
                        Width = i.Width,
                        VoteCount = i.VoteCount,
                        ArtistId = i.ArtistId,
                        Type = i.Type,
                        Site = i.Site,
                    }
            )
            .RunAsync();
    }

    public async Task StoreRelease(Guid releaseGroupId)
    {
        try
        {
            using FanArtMusicClient fanArtMusicClient = new();
            FanArtAlbum? fanArt = await fanArtMusicClient.Album(releaseGroupId);
            if (fanArt is null)
                return;

            await using MediaContext mediaContext = new();
            await StoreReleaseImages(mediaContext, fanArt, releaseGroupId);
        }
        catch (Exception e)
        {
            if (e.Message.Contains("404"))
                return;
            Log.LogTrace(e.Message);
        }
    }

    public async Task StoreReleaseImages(
        MediaContext mediaContext,
        FanArtAlbum fanArt,
        Guid releaseGroupId
    )
    {
        List<Image> covers = [];
        List<Image> cdArts = [];
        foreach ((Guid albumId, Albums albums) in fanArt.Albums)
        {
            covers.AddRange(
                albums.Cover.Select(image => new Image
                {
                    AspectRatio = 1,
                    Type = "cover",
                    VoteCount = image.Likes,
                    FilePath = "/" + image.Url.FileName(),
                    AlbumId = albumId,
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
                    AlbumId = albumId,
                    Site = image.Url.BasePath(),
                    Name = fanArt.Name,
                })
            );
        }

        ReleaseGroup dbRelease = await mediaContext
            .ReleaseGroups.Include(a => a.AlbumReleaseGroup)
                .ThenInclude(a => a.Album)
            .FirstAsync(a => a.Id == releaseGroupId);

        IEnumerable<Image> images = covers
            .Concat(cdArts)
            .Where(image => dbRelease.AlbumReleaseGroup.Any(ar => ar.AlbumId == image.AlbumId));

        string? albumCover = await CoverFiles.StoreFirstFanArtAsync(
            fanArt.Albums.Values.SelectMany(album => album.Cover).Select(image => image.Url)
        );

        dbRelease.Cover = albumCover ?? dbRelease.Cover;

        foreach (AlbumReleaseGroup albumRelease in dbRelease.AlbumReleaseGroup)
        {
            albumRelease.Album.Cover = albumCover ?? albumRelease.Album.Cover;
        }

        await mediaContext.SaveChangesAsync();

        await mediaContext
            .Images.UpsertRange(images)
            .On(v => new { v.FilePath, v.AlbumId })
            .WhenMatched(
                (s, i) =>
                    new()
                    {
                        AspectRatio = i.AspectRatio,
                        Name = i.Name,
                        Height = i.Height,
                        FilePath = i.FilePath,
                        Width = i.Width,
                        VoteCount = i.VoteCount,
                        AlbumId = i.AlbumId,
                        Type = i.Type,
                        Site = i.Site,
                    }
            )
            .RunAsync();
    }
}
