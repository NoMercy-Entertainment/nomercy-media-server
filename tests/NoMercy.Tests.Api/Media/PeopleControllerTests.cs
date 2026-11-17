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

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.People;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.People;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "People")]
public class PeopleControllerTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;
    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;

    public PeopleControllerTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
    }

    [Fact]
    public async Task Index_Authenticated_ReturnsPaginatedEnvelope()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/person?take=10&page=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);

        Assert.True(
            document.RootElement.TryGetProperty("data", out JsonElement data),
            "Paginated response must expose a 'data' array"
        );
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.True(document.RootElement.TryGetProperty("has_more", out _));
    }

    [Fact]
    public async Task Index_Unauthenticated_DoesNotReturnOk()
    {
        HttpResponseMessage response = await _unauthed.GetAsync("/api/v1/person?take=10&page=0");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Show_ProviderThrows_ReturnsNotFoundNotServerError()
    {
        Mock<IPersonMetadataProvider> providerMock = new();
        providerMock
            .Setup(p => p.GetPersonAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TMDB provider unreachable"));

        HttpClient client = _factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IPersonMetadataProvider>();
                    services.AddSingleton(providerMock.Object);
                });
            })
            .CreateClient()
            .AsAuthenticated();

        HttpResponseMessage response = await client.GetAsync("/api/v1/person/123");

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("title", out JsonElement title)
            .Should()
            .BeTrue("a provider failure must still return the standard ProblemDetails envelope");
        title.GetString().Should().Be("Not Found.");
    }

    private const int OwnedMovieId = 990001;
    private const int MissingTvId = 990002;

    private HttpClient PersonClient()
    {
        TmdbPersonAppends appends = new()
        {
            Id = 990100,
            Name = "Test Person",
            Biography = "A made-up biography.",
            CombinedCredits = new()
            {
                Cast =
                [
                    new()
                    {
                        Id = OwnedMovieId,
                        MediaType = "movie",
                        Title = "Owned Film",
                        Character = "Test Hero",
                        BackdropPath = "/owned-backdrop.jpg",
                        PosterPath = "/owned-poster.jpg",
                        VoteCount = 500,
                    },
                    new()
                    {
                        Id = MissingTvId,
                        MediaType = "tv",
                        Name = "Missing Show",
                        Character = "Nobody",
                        VoteCount = 10,
                    },
                ],
                Crew =
                [
                    new()
                    {
                        Id = OwnedMovieId,
                        MediaType = "movie",
                        Title = "Owned Film",
                        Job = "Director",
                    },
                    new()
                    {
                        Id = OwnedMovieId,
                        MediaType = "movie",
                        Title = "Owned Film",
                        Job = "Sound Editor",
                    },
                ],
            },
        };

        Movie ownedFilm = new()
        {
            Id = OwnedMovieId,
            Title = "Owned Film",
            VideoFiles =
            [
                new()
                {
                    Id = Ulid.NewUlid(),
                    Filename = "Owned.Film.mkv",
                    Folder = "/media/movies/Owned Film",
                    HostFolder = "/media/movies/Owned Film",
                    Share = NoMercyApiFactory.MovieFolderId.ToString(),
                    MovieId = OwnedMovieId,
                },
            ],
        };

        Person person = new()
        {
            Id = 990100,
            Name = "Test Person",
            Casts =
            [
                new()
                {
                    MovieId = OwnedMovieId,
                    Role = new() { Character = "Test Hero" },
                    Movie = ownedFilm,
                },
            ],
            Crews =
            [
                new()
                {
                    MovieId = OwnedMovieId,
                    Job = new() { Task = "Director" },
                    Movie = ownedFilm,
                },
                new()
                {
                    MovieId = OwnedMovieId,
                    Job = new() { Task = "Sound Editor" },
                    Movie = ownedFilm,
                },
            ],
        };

        Mock<IPersonMetadataProvider> providerMock = new();
        providerMock
            .Setup(p => p.GetPersonAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(appends);

        Mock<IPeopleRepository> repositoryMock = new();
        repositoryMock
            .Setup(r => r.GetPersonWithCreditsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(person);

        return _factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IPersonMetadataProvider>();
                    services.AddSingleton(providerMock.Object);
                    services.RemoveAll<IPeopleRepository>();
                    services.AddSingleton(repositoryMock.Object);
                });
            })
            .CreateClient()
            .AsAuthenticated();
    }

    [Fact]
    public async Task Show_WithoutVersion_KeepsThePersonResponseDto()
    {
        HttpResponseMessage response = await PersonClient().GetAsync("/api/v1/person/990100");

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        data.GetProperty("name").GetString().Should().Be("Test Person");
        data.TryGetProperty("combined_credits", out _).Should().BeTrue();
        data.TryGetProperty("known_for", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Show_LolomoVersion_ReturnsAHeroAndTheCreditRowsTheUserOwns()
    {
        HttpResponseMessage response = await PersonClient()
            .GetAsync("/api/v1/person/990100?version=lolomo");

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement[] page = doc.RootElement.GetProperty("data").EnumerateArray().ToArray();
        page.Select(c => c.GetProperty("component").GetString())
            .Should()
            .Equal("NMHomeCard", "NMLolomo");

        JsonElement hero = page[0].GetProperty("props").GetProperty("data");
        hero.GetProperty("title").GetString().Should().Be("Test Person");
        hero.GetProperty("overview").GetString().Should().Be("A made-up biography.");
        hero.GetProperty("backdrop").GetString().Should().Be("/owned-backdrop.jpg");

        JsonElement lolomo = page[1].GetProperty("props");
        lolomo.GetProperty("hero").GetString().Should().Be("none");
        JsonElement[] rows = lolomo.GetProperty("items").EnumerateArray().ToArray();
        rows.Select(r => r.GetProperty("props").GetProperty("title").GetString())
            .Should()
            .Equal("Known for", "Acting", "Director", "Crew");

        JsonElement Row(string title) =>
            rows.Single(r => r.GetProperty("props").GetProperty("title").GetString() == title)
                .GetProperty("props")
                .GetProperty("items");

        string Subtitle(string title) =>
            Row(title)[0]
                .GetProperty("props")
                .GetProperty("data")
                .GetProperty("subtitle")
                .GetString()!;

        Subtitle("Acting").Should().Be("Test Hero");
        Subtitle("Director").Should().Be("Director");
        Subtitle("Crew").Should().Be("Sound Editor");
        Row("Acting").GetArrayLength().Should().Be(1);
    }
}
