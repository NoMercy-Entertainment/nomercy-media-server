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

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Controllers.V1.Music;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using Xunit;
using MusicGenresController = NoMercy.Api.Controllers.V1.Music.GenresController;

namespace NoMercy.Tests.Api.Music;

/// <summary>
/// Music cards with no image of their own (every genre, a playlist without a
/// stored cover) answer NMGroupCard with square album covers, so the TV no
/// longer draws them as flat logo tiles.
/// </summary>
[Trait("Category", "Unit")]
public class MusicGroupCardTests
{
    private static readonly Guid RockId = Guid.Parse("6f1c2a4e-0000-4000-8000-000000000001");
    private static readonly Guid MixId = Guid.Parse("6f1c2a4e-0000-4000-8000-000000000002");
    private static readonly Guid CoveredId = Guid.Parse("6f1c2a4e-0000-4000-8000-000000000003");

    private static readonly PaletteColors AlbumPalette = new()
    {
        Primary = "#336699",
        DarkVibrant = "#336699",
    };

    private static readonly ControllerContext SignedIn = new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new(
                new ClaimsIdentity(
                    [new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                    "test"
                )
            ),
        },
    };

    // An album cover as the repository returns it: the stored path, which the
    // card turns into the URL the server serves it on.
    private static GroupPoster Cover(string path) => new(path, AlbumPalette, "#336699");

    private static MusicGenreCardDto Rock() =>
        new()
        {
            Id = RockId,
            Name = "rock",
            TrackCount = 12,
            Link = new($"/music/genres/{RockId}", UriKind.Relative),
            ItemPosters = [Cover("/a.jpg"), Cover("/b.jpg")],
        };

    private static PlaylistCardDto Mix() =>
        new()
        {
            Id = MixId,
            Name = "Road trip",
            TrackCount = 3,
            Link = new($"/music/playlists/{MixId}", UriKind.Relative),
            ItemPosters = [Cover("/a.jpg")],
        };

    private static PlaylistCardDto Covered() =>
        new()
        {
            Id = CoveredId,
            Name = "Covered",
            Cover = "/covered.jpg",
            ColorPalette = """{"cover":{"primary":"#112233"}}""",
            TrackCount = 5,
            Link = new($"/music/playlists/{CoveredId}", UriKind.Relative),
        };

    private static async Task<List<ComponentEnvelope>> StartPage(string? version)
    {
        Mock<IMusicRepository> repository = new();
        repository
            .Setup(r => r.GetMusicStartPageAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new MusicStartPageData { LatestGenres = [Rock()], Playlists = [Mix(), Covered()] }
            );
        MusicController controller = new(repository.Object) { ControllerContext = SignedIn };

        IActionResult result = await controller.Index(new PageRequestDto { Version = version });
        List<ComponentEnvelope> page =
        [
            .. Assert.IsType<ComponentResponse>(Assert.IsType<OkObjectResult>(result).Value).Data,
        ];
        return version == "lolomo"
            ? [.. Assert.IsType<LolomoProps>(Assert.Single(page).Props).Items]
            : page;
    }

    private static List<ComponentEnvelope> RowItems(List<ComponentEnvelope> rows, string id) =>
        [
            .. (
                (ContainerProps)
                    rows.Single(row => ((ContainerProps)row.Props).Id?.ToString() == id).Props
            ).Items,
        ];

    private static JObject Json(ComponentEnvelope card) =>
        JObject.Parse(JsonConvert.SerializeObject(card));

    [Fact]
    public async Task StartPage_GenresRow_AnswersSquareGroupCards_FromAlbumCovers()
    {
        ComponentEnvelope card = Assert.Single(RowItems(await StartPage("lolomo"), "genres"));

        Assert.Equal("NMGroupCard", card.Component);
        JObject data = (JObject)Json(card)["props"]!["data"]!;
        Assert.Equal("square", (string?)data["item_shape"]);
        Assert.Equal(JTokenType.Integer, data["id"]!.Type);
        Assert.Equal("Rock", (string?)data["title"]);
        Assert.Equal($"/music/genres/{RockId}", (string?)data["link"]);
        Assert.Equal(
            ["/images/music/a.jpg", "/images/music/b.jpg"],
            data["item_posters"]!.Select(poster => (string?)poster["src"])
        );
        Assert.Equal("/images/music/a.jpg", (string?)data["item_posters"]![0]!["url"]);
    }

    [Fact]
    public async Task StartPage_PlaylistWithoutCover_AnswersGroupCard_AndCoveredOneStaysMusicCard()
    {
        List<ComponentEnvelope> playlists = RowItems(await StartPage(null), "playlists");

        Assert.Equal(["NMGroupCard", "NMMusicCard"], playlists.Select(card => card.Component));
        JObject data = (JObject)Json(playlists[0])["props"]!["data"]!;
        Assert.Equal("square", (string?)data["item_shape"]);
        Assert.Equal("Road trip", (string?)data["title"]);
        Assert.Equal($"/music/playlists/{MixId}", (string?)data["link"]);
    }

    [Fact]
    public async Task StartPlaylistsRow_PlaylistWithoutCover_AnswersGroupCard()
    {
        Mock<IMusicRepository> repository = new();
        repository
            .Setup(r =>
                r.GetPlaylistCardsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([Mix(), Covered()]);
        MusicController controller = new(repository.Object) { ControllerContext = SignedIn };

        IActionResult result = await controller.Playlists(new());
        ComponentEnvelope row = Assert.Single(
            Assert.IsType<ComponentResponse>(Assert.IsType<OkObjectResult>(result).Value).Data
        );

        Assert.Equal(
            ["NMGroupCard", "NMMusicCard"],
            ((ContainerProps)row.Props).Items.Select(card => card.Component)
        );
    }

    [Theory]
    [InlineData("lolomo")]
    [InlineData(null)]
    public async Task GenresPage_AnswersSquareGroupCards_FromAlbumCovers(string? version)
    {
        Mock<IGenreRepository> repository = new();
        repository
            .Setup(r => r.GetMusicGenreCardsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Rock()]);
        MusicGenresController controller = new(repository.Object) { ControllerContext = SignedIn };

        IActionResult result = await controller.Index(new PageRequestDto { Version = version });
        ComponentEnvelope page = Assert.Single(
            Assert.IsType<ComponentResponse>(Assert.IsType<OkObjectResult>(result).Value).Data
        );
        ComponentEnvelope row =
            version == "lolomo"
                ? Assert.Single(Assert.IsType<LolomoProps>(page.Props).Items)
                : page;
        ComponentEnvelope card = Assert.Single(((ContainerProps)row.Props).Items);

        Assert.Equal("NMGroupCard", card.Component);
        JObject data = (JObject)Json(card)["props"]!["data"]!;
        Assert.Equal("square", (string?)data["item_shape"]);
        Assert.Equal(JTokenType.Integer, data["id"]!.Type);
        Assert.Equal(
            ["/images/music/a.jpg", "/images/music/b.jpg"],
            data["item_posters"]!.Select(poster => (string?)poster["src"])
        );
    }

    // Old clients decode the anime card as before: same keys plus the additive
    // item_shape, and a numeric id.
    [Fact]
    public void AnimeGroupCard_KeepsItsWireShape()
    {
        GroupCardData card = new(
            new AnimeThemeWithCountsDto
            {
                Id = 7,
                Name = "action",
                TvShowsWithVideo = 1,
                ItemPosters = [new GroupPoster("/a.jpg", null)],
            }
        );

        JObject json = JObject.Parse(JsonConvert.SerializeObject(card));

        Assert.Equal(
            [
                "id",
                "title",
                "titleSort",
                "type",
                "link",
                "year",
                "quarter",
                "number_of_items",
                "have_items",
                "item_posters",
                "item_shape",
                "display_title",
                "subtitle",
                "icon",
                "accent_color",
                "overview",
            ],
            json.Properties().Select(property => property.Name)
        );
        Assert.Equal(JTokenType.Integer, json["id"]!.Type);
        Assert.Equal(7, (int)json["id"]!);
        Assert.Equal("poster", (string?)json["item_shape"]);
        Assert.Equal(
            ["src", "color", "url"],
            ((JObject)json["item_posters"]![0]!).Properties().Select(property => property.Name)
        );
        Assert.StartsWith("http", (string?)json["item_posters"]![0]!["url"]);
    }

    // Stoney, 2026-10-06: "the playlist of the genre also needs the same image as the card with the
    // color palette"; 2026-10-07: "the list screen is still missing the cover for music genre".
    [Fact]
    public async Task GenrePage_CarriesTheCardsCovers_WithTheirPalette()
    {
        Mock<IGenreRepository> repository = new();
        repository
            .Setup(r =>
                r.GetMusicGenreAsync(It.IsAny<Guid>(), RockId, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new NoMercy.Database.Models.Music.MusicGenre { Id = RockId, Name = "rock" }
            );
        repository
            .Setup(r =>
                r.GetMusicGenreCoversAsync(It.IsAny<Guid>(), RockId, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([Cover("/a.jpg"), Cover("/b.jpg")]);
        MusicGenresController controller = new(repository.Object) { ControllerContext = SignedIn };

        IActionResult result = await controller.Show(RockId);

        JObject data = (JObject)
            JObject.Parse(JsonConvert.SerializeObject(Assert.IsType<OkObjectResult>(result).Value))[
                "data"
            ]!;
        Assert.Equal(
            ["/images/music/a.jpg", "/images/music/b.jpg"],
            data["item_posters"]!.Select(poster => (string?)poster["src"])
        );
        Assert.Equal("#336699", (string?)data["item_posters"]![0]!["color_palette"]!["primary"]);
    }
}
