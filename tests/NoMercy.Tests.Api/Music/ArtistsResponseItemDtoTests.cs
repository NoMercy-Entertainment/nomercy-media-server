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

using NoMercy.Api.DTOs.Music;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Music;
using Xunit;

namespace NoMercy.Tests.Api.Music;

[Trait("Category", "Unit")]
public class ArtistsResponseItemDtoTests
{
    private static Artist BuildArtist(
        string? cover = "/artist-cover.jpg",
        Image[]? images = null,
        ArtistTrack[]? artistTracks = null,
        ColorPalette? colorPalette = null
    )
    {
        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Artist",
            Description = "Artist description",
            Disambiguation = "The Band",
            Cover = cover,
        };
        foreach (Image image in images ?? [])
            artist.Images.Add(image);
        foreach (ArtistTrack artistTrack in artistTracks ?? [])
            artist.ArtistTrack.Add(artistTrack);
        if (colorPalette is not null)
            artist.ColorPalette = colorPalette;

        return artist;
    }

    private static Album BuildAlbum(
        string? cover = "/album-cover.jpg",
        Image[]? images = null,
        AlbumTrack[]? albumTracks = null
    )
    {
        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Album",
            Description = "Album description",
            Disambiguation = "Deluxe",
            Cover = cover,
        };
        foreach (Image image in images ?? [])
            album.Images.Add(image);
        foreach (AlbumTrack albumTrack in albumTracks ?? [])
            album.AlbumTrack.Add(albumTrack);

        return album;
    }

    private static ArtistTrack BuildArtistTrack(Guid artistId)
    {
        Track track = new() { Id = Guid.NewGuid(), Name = "Track" };
        return new() { ArtistId = artistId, Track = track };
    }

    private static AlbumTrack BuildAlbumTrackForArtist(Guid albumId)
    {
        Track track = new() { Id = Guid.NewGuid(), Name = "Track" };
        return new() { AlbumId = albumId, Track = track };
    }

    [Fact]
    public void ArtistCtor_CoverPresent_UsesArtistCover()
    {
        Artist artist = BuildArtist(cover: "/artist-cover.jpg");

        ArtistsResponseItemDto dto = new(artist);

        dto.Cover.Should().Be("/images/music/artist-cover.jpg");
    }

    [Fact]
    public void ArtistCtor_CoverNull_FallsBackToFirstImageFilePath()
    {
        Artist artist = BuildArtist(
            cover: null,
            images: [new() { Type = "thumb", FilePath = "/first-image.jpg" }]
        );

        ArtistsResponseItemDto dto = new(artist);

        dto.Cover.Should().Be("/images/music/first-image.jpg");
    }

    [Fact]
    public void ArtistCtor_CoverAndImagesEmpty_CoverIsNull()
    {
        Artist artist = BuildArtist(cover: null, images: []);

        ArtistsResponseItemDto dto = new(artist);

        dto.Cover.Should().BeNull();
    }

    [Fact]
    public void ArtistCtor_TracksCountsAllArtistTrackEntries()
    {
        Artist artist = BuildArtist();
        Guid artistId = artist.Id;
        artist.ArtistTrack.Add(BuildArtistTrack(artistId));
        artist.ArtistTrack.Add(BuildArtistTrack(artistId));

        ArtistsResponseItemDto dto = new(artist);

        dto.Tracks.Should().Be(2);
    }

    [Fact]
    public void ArtistCtor_SetsFieldsCorrectly()
    {
        ColorPalette palette = new() { Cover = new() { Dominant = "#111111" } };
        Artist artist = BuildArtist(colorPalette: palette);
        Guid artistId = artist.Id;

        ArtistsResponseItemDto dto = new(artist);

        dto.Id.Should().Be(artistId);
        dto.Name.Should().Be("Test Artist");
        dto.Disambiguation.Should().Be("The Band");
        dto.Description.Should().Be("Artist description");
        dto.Type.Should().Be("artist");
        dto.Link.ToString().Should().Be($"/music/artists/{artistId}");
        dto.ColorPalette.Should().NotBeNull();
        dto.ColorPalette!.Cover!.Dominant.Should().Be("#111111");
    }

    [Fact]
    public void AlbumCtor_CoverPresent_UsesAlbumCover()
    {
        Album album = BuildAlbum(cover: "/album-cover.jpg");

        ArtistsResponseItemDto dto = new(album);

        dto.Cover.Should().Be("/images/music/album-cover.jpg");
    }

    [Fact]
    public void AlbumCtor_CoverNull_FallsBackToFirstImageFilePath()
    {
        Album album = BuildAlbum(
            cover: null,
            images: [new() { Type = "background", FilePath = "/album-first-image.jpg" }]
        );

        ArtistsResponseItemDto dto = new(album);

        dto.Cover.Should().Be("/images/music/album-first-image.jpg");
    }

    [Fact]
    public void AlbumCtor_TracksCountsAlbumTrackEntries()
    {
        Album album = BuildAlbum();
        Guid albumId = album.Id;
        album.AlbumTrack.Add(BuildAlbumTrackForArtist(albumId));
        album.AlbumTrack.Add(BuildAlbumTrackForArtist(albumId));
        album.AlbumTrack.Add(BuildAlbumTrackForArtist(albumId));

        ArtistsResponseItemDto dto = new(album);

        dto.Tracks.Should().Be(3);
    }

    [Fact]
    public void AlbumCtor_SetsFieldsCorrectly_TypeIsAlbumWithAlbumLink()
    {
        Album album = BuildAlbum();
        Guid albumId = album.Id;

        ArtistsResponseItemDto dto = new(album);

        dto.Id.Should().Be(albumId);
        dto.Name.Should().Be("Test Album");
        dto.Disambiguation.Should().Be("Deluxe");
        dto.Description.Should().Be("Album description");
        dto.Type.Should().Be("album");
        dto.Link.ToString().Should().Be($"/music/albums/{albumId}");
    }

    private static ArtistCardDto BuildCard(
        string? cover = "/card-cover.jpg",
        string? thumbImagePath = null,
        string? colorPaletteJson = ""
    )
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Name = "Card Artist",
            Cover = cover,
            Disambiguation = "Card Disambiguation",
            Description = "Card description",
            ColorPalette = colorPaletteJson ?? string.Empty,
            LibraryId = Ulid.NewUlid(),
            Folder = "/music/card-artist",
            TrackCount = 7,
            ThumbImagePath = thumbImagePath,
        };
    }

    [Fact]
    public void CardCtor_CoverPresent_UsesCardCover()
    {
        ArtistCardDto card = BuildCard(cover: "/card-cover.jpg");

        ArtistsResponseItemDto dto = new(card);

        dto.Cover.Should().Be("/images/music/card-cover.jpg");
    }

    [Fact]
    public void CardCtor_CoverNull_FallsBackToThumbImagePath()
    {
        ArtistCardDto card = BuildCard(cover: null, thumbImagePath: "/thumb.jpg");

        ArtistsResponseItemDto dto = new(card);

        dto.Cover.Should().Be("/images/music/thumb.jpg");
    }

    [Fact]
    public void CardCtor_CoverAndThumbNull_CoverIsNull()
    {
        ArtistCardDto card = BuildCard(cover: null, thumbImagePath: null);

        ArtistsResponseItemDto dto = new(card);

        dto.Cover.Should().BeNull();
    }

    [Fact]
    public void CardCtor_ColorPaletteFromJson_IsParsed()
    {
        ArtistCardDto card = BuildCard(colorPaletteJson: """{"cover":{"dominant":"#222222"}}""");

        ArtistsResponseItemDto dto = new(card);

        dto.ColorPalette.Should().NotBeNull();
        dto.ColorPalette!.Cover!.Dominant.Should().Be("#222222");
    }

    [Fact]
    public void CardCtor_ColorPaletteJsonEmpty_ColorPaletteIsNull()
    {
        ArtistCardDto card = BuildCard(colorPaletteJson: "");

        ArtistsResponseItemDto dto = new(card);

        dto.ColorPalette.Should().BeNull();
    }

    [Fact]
    public void CardCtor_TracksUsesTrackCount()
    {
        ArtistCardDto card = BuildCard();

        ArtistsResponseItemDto dto = new(card);

        dto.Tracks.Should().Be(7);
    }

    [Fact]
    public void CardCtor_SetsFieldsCorrectly()
    {
        ArtistCardDto card = BuildCard();
        Guid cardId = card.Id;

        ArtistsResponseItemDto dto = new(card);

        dto.Id.Should().Be(cardId);
        dto.Name.Should().Be("Card Artist");
        dto.Disambiguation.Should().Be("Card Disambiguation");
        dto.Description.Should().Be("Card description");
        dto.Type.Should().Be("artist");
        dto.Link.ToString().Should().Be($"/music/artists/{cardId}");
    }
}
