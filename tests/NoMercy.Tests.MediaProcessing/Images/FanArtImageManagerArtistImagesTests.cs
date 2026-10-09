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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.MediaProcessing.Images;
using NoMercy.Providers.FanArt.Models;
using Image = NoMercy.Database.Models.Media.Image;

namespace NoMercy.Tests.MediaProcessing.Images;

/// <summary>
/// An artist's FanArt response was fetched only for its "artistthumb" — used to
/// build a cover-palette colour — and every other image type on the same
/// response (background, logo, hd logo, banner) was read off the wire and
/// discarded. Reproduced live: an artist as well-covered as Robbie Williams had
/// zero backdrop, because nothing ever persisted one. This exercises the
/// persistence half directly, which <c>FanArtImageManager.Add</c> now calls
/// alongside the palette fetch it already did.
/// </summary>
public sealed class FanArtImageManagerArtistImagesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;

    public FanArtImageManagerArtistImagesTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task StoreArtistImages_FullFanArtResponse_PersistsEveryImageType()
    {
        Guid artistId = Guid.NewGuid();
        FanArtArtistDetails fanArt = new()
        {
            Thumbs = [FanArtImage("thumb.jpg")],
            Backgrounds = [FanArtImage("background.jpg")],
            Logos = [FanArtImage("logo.png")],
            HdLogos = [FanArtImage("hdlogo.png")],
            Banners = [FanArtImage("banner.jpg")],
        };

        FanArtImageManager manager = new(new(_context));
        ICollection<Image> stored = await manager.StoreArtistImages(
            fanArt,
            artistId,
            new() { Id = artistId }
        );

        stored.Should().HaveCount(5);
        stored.Should().Contain(image => image.Type == "background");
        stored.Should().Contain(image => image.Type == "logo");
        stored.Should().Contain(image => image.Type == "hdLogo");
        stored.Should().Contain(image => image.Type == "banner");
        stored.Should().Contain(image => image.Type == "thumb");
        stored.Should().OnlyContain(image => image.ArtistId == artistId);

        (await _context.Images.CountAsync(image => image.ArtistId == artistId)).Should().Be(5);
    }

    private static NoMercy.Providers.FanArt.Models.Image FanArtImage(string fileName) =>
        new() { Url = new($"https://assets.fanart.tv/fanart/music/{Guid.NewGuid()}/{fileName}") };
}
