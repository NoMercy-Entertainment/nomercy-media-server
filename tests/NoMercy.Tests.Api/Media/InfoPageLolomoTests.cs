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
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.People;
using NoMercy.Database.Models.TvShows;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "InfoLolomo")]
public class InfoPageLolomoTests : IClassFixture<NoMercyApiFactory>
{
    private const int MovieId = 990201;
    private const int TvId = 990301;
    private const int CollectionId = 990401;

    private readonly NoMercyApiFactory _factory;

    public InfoPageLolomoTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient ClientWith(Action<IServiceCollection> configure) =>
        _factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(configure))
            .CreateClient()
            .AsAuthenticated();

    private static Cast CastOf(int personId, string name, string? profile, int? order) =>
        new()
        {
            PersonId = personId,
            Person = new()
            {
                Id = personId,
                Name = name,
                Profile = profile,
            },
            Role = new() { Character = $"{name} Role", Order = order },
        };

    private static Crew CrewOf(int personId, string name, string? profile, string job) =>
        new()
        {
            PersonId = personId,
            Person = new()
            {
                Id = personId,
                Name = name,
                Profile = profile,
            },
            Job = new() { Task = job, Order = 1 },
        };

    private static List<Cast> CastList() =>
        [
            CastOf(5001, "Alex", null, 1),
            CastOf(5002, "Bram", "/bram.jpg", 5),
            CastOf(5003, "Cara", "/cara.jpg", 2),
            CastOf(5004, "Dina", "/dina.jpg", null),
            CastOf(5005, "Cara", "/cara-two.jpg", 3),
        ];

    private static List<Crew> CrewList() =>
        [
            CrewOf(6001, "Wim Writer", "/wim.jpg", "Writer"),
            CrewOf(6002, "Dora Director", "/dora.jpg", "Director"),
            CrewOf(6003, "Sam Sound", "/sam.jpg", "Sound Editor"),
        ];

    private HttpClient MovieClient()
    {
        Movie movie = new()
        {
            Id = MovieId,
            Title = "Test Film",
            Cast = CastList(),
            Crew = CrewList(),
        };

        Mock<IMovieRepository> repository = new();
        repository
            .Setup(r =>
                r.GetMovieDetailAsync(
                    It.IsAny<Guid>(),
                    MovieId,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(movie);

        return ClientWith(services =>
        {
            services.RemoveAll<IMovieRepository>();
            services.AddSingleton(repository.Object);
        });
    }

    private static Season SeasonOf(int number, string title, params string[] episodes) =>
        new()
        {
            Id = 7000 + number,
            SeasonNumber = number,
            Title = title,
            TvId = TvId,
            Episodes =
            [
                .. episodes.Select(
                    (name, index) =>
                        new Episode
                        {
                            Id = (7000 + number) * 100 + index,
                            TvId = TvId,
                            SeasonNumber = number,
                            EpisodeNumber = index + 1,
                            Title = name,
                            Still = $"/still-{number}-{index + 1}.jpg",
                        }
                ),
            ],
        };

    private HttpClient TvClient()
    {
        Season one = SeasonOf(1, "Season One", "Pilot", "Second");
        Season two = SeasonOf(2, "Season Two", "Return");
        Tv tv = new()
        {
            Id = TvId,
            Title = "Test Show",
            Seasons = [one, two],
            Episodes = [.. one.Episodes, .. two.Episodes],
            Cast = CastList(),
            Crew = CrewList(),
        };

        Mock<ITvShowRepository> repository = new();
        repository
            .Setup(r =>
                r.GetTvAsync(
                    It.IsAny<Guid>(),
                    TvId,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new TvDetail(tv, [], []));

        return ClientWith(services =>
        {
            services.RemoveAll<ITvShowRepository>();
            services.AddSingleton(repository.Object);
        });
    }

    private HttpClient CollectionClient()
    {
        Movie first = new()
        {
            Id = 8001,
            Title = "Part One",
            Cast = CastList(),
            Crew = CrewList(),
        };
        Movie second = new() { Id = 8002, Title = "Part Two" };

        Collection collection = new()
        {
            Id = CollectionId,
            Title = "Test Saga",
            Parts = 2,
            Images = [new() { FilePath = "/c.jpg", Type = "backdrop" }],
            CollectionMovies =
            [
                new()
                {
                    CollectionId = CollectionId,
                    MovieId = 8001,
                    Movie = first,
                },
                new()
                {
                    CollectionId = CollectionId,
                    MovieId = 8002,
                    Movie = second,
                },
                new()
                {
                    CollectionId = CollectionId,
                    MovieId = 8001,
                    Movie = first,
                },
            ],
        };

        Mock<ICollectionRepository> repository = new();
        repository
            .Setup(r =>
                r.GetCollectionAsync(
                    It.IsAny<Guid>(),
                    CollectionId,
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(collection);

        return ClientWith(services =>
        {
            services.RemoveAll<ICollectionRepository>();
            services.AddSingleton(repository.Object);
        });
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static JsonElement Lolomo(JsonDocument doc, string expectedId)
    {
        JsonElement[] page = doc.RootElement.GetProperty("data").EnumerateArray().ToArray();
        page.Select(c => c.GetProperty("component").GetString()).Should().Equal("NMLolomo");

        JsonElement props = page[0].GetProperty("props");
        props.GetProperty("id").GetString().Should().Be(expectedId);
        props.GetProperty("hero").GetString().Should().Be("none");
        return props.GetProperty("items");
    }

    private static string[] RowIds(JsonElement rows) =>
        rows.EnumerateArray()
            .Select(r => r.GetProperty("props").GetProperty("id").GetString()!)
            .ToArray();

    private static JsonElement Row(JsonElement rows, string id) =>
        rows.EnumerateArray()
            .Single(r => r.GetProperty("props").GetProperty("id").GetString() == id)
            .GetProperty("props");

    private static JsonElement[] Cards(JsonElement row) =>
        row.GetProperty("items")
            .EnumerateArray()
            .Select(c => c.GetProperty("props").GetProperty("data"))
            .ToArray();

    [Fact]
    public async Task Movie_LolomoVersion_ReturnsOneLolomoWithWriterDirectorCastAndCrewRows()
    {
        using JsonDocument doc = await GetJson(
            MovieClient(),
            $"/api/v1/movie/{MovieId}?version=lolomo"
        );

        JsonElement rows = Lolomo(doc, $"info-movie-{MovieId}");
        RowIds(rows).Should().Equal("info-director-writer", "info-cast", "info-crew");
        Row(rows, "info-director-writer")
            .GetProperty("title")
            .GetString()
            .Should()
            .Be("Writer & Director");
        Row(rows, "info-cast").GetProperty("title").GetString().Should().Be("Cast");
        Row(rows, "info-crew").GetProperty("title").GetString().Should().Be("Crew");
    }

    [Fact]
    public async Task Tv_LolomoVersion_ReturnsSeasonRowsBeforeWriterDirectorCastAndCrew()
    {
        using JsonDocument doc = await GetJson(TvClient(), $"/api/v1/tv/{TvId}?version=lolomo");

        JsonElement rows = Lolomo(doc, $"info-tv-{TvId}");
        RowIds(rows)
            .Should()
            .Equal(
                "info-seasons-1",
                "info-seasons-2",
                "info-director-writer",
                "info-cast",
                "info-crew"
            );
    }

    [Fact]
    public async Task Tv_LolomoVersion_ReturnsOneSeasonCarouselPerSeasonWithEpisodeCards()
    {
        using JsonDocument doc = await GetJson(TvClient(), $"/api/v1/tv/{TvId}?version=lolomo");
        JsonElement rows = Lolomo(doc, $"info-tv-{TvId}");

        JsonElement one = Row(rows, "info-seasons-1");
        one.GetProperty("title").GetString().Should().Be("1 - Season One");
        one.GetProperty("properties").GetProperty("rowType").GetString().Should().Be("seasons");
        one.GetProperty("properties").GetProperty("season_number").GetString().Should().Be("1");

        JsonElement[] episodes = Cards(one);
        episodes
            .Select(e => e.GetProperty("title").GetString())
            .Should()
            .Equal("1. Pilot", "2. Second");
        episodes[0].GetProperty("type").GetString().Should().Be("episode");
        episodes[0].GetProperty("backdrop").GetString().Should().Be("/still-1-1.jpg");
        episodes[0]
            .GetProperty("link")
            .GetString()
            .Should()
            .Be($"/tv/{TvId}/watch?season=1&episode=1");

        JsonElement two = Row(rows, "info-seasons-2");
        two.GetProperty("title").GetString().Should().Be("2 - Season Two");
        two.GetProperty("properties").GetProperty("season_number").GetString().Should().Be("2");
        Cards(two).Select(e => e.GetProperty("title").GetString()).Should().Equal("1. Return");
    }

    [Fact]
    public async Task Collection_LolomoVersion_ReturnsCollectionRowBeforeCastAndCrewWithDistinctCards()
    {
        using JsonDocument doc = await GetJson(
            CollectionClient(),
            $"/api/v1/collection/{CollectionId}?version=lolomo"
        );

        JsonElement rows = Lolomo(doc, $"info-collection-{CollectionId}");
        RowIds(rows).Should().Equal("info-collection", "info-cast", "info-crew");

        JsonElement collection = Row(rows, "info-collection");
        collection.GetProperty("title").GetString().Should().Be("Collections");
        Cards(collection)
            .Select(c => c.GetProperty("title").GetString())
            .Should()
            .Equal("Part One", "Part Two");
    }

    [Fact]
    public async Task Movie_LolomoVersion_SortsCastByProfileThenOrderThenNameAndCaptionsEveryCard()
    {
        using JsonDocument doc = await GetJson(
            MovieClient(),
            $"/api/v1/movie/{MovieId}?version=lolomo"
        );
        JsonElement rows = Lolomo(doc, $"info-movie-{MovieId}");

        JsonElement[] cast = Cards(Row(rows, "info-cast"));
        cast.Select(c => c.GetProperty("title").GetString())
            .Should()
            .Equal("Cara", "Bram", "Dina", "Alex");
        cast.Select(c => c.GetProperty("subtitle").GetString())
            .Should()
            .Equal("Cara Role", "Bram Role", "Dina Role", "Alex Role");
        cast[0].GetProperty("type").GetString().Should().Be("person");
        cast[0].GetProperty("poster").GetString().Should().Be("/cara.jpg");

        Cards(Row(rows, "info-director-writer"))
            .Select(c => c.GetProperty("title").GetString())
            .Should()
            .Equal("Wim Writer", "Dora Director");
        Cards(Row(rows, "info-director-writer"))
            .Select(c => c.GetProperty("subtitle").GetString())
            .Should()
            .Equal("Writer", "Director");

        Cards(Row(rows, "info-crew"))
            .Select(c => c.GetProperty("subtitle").GetString())
            .Should()
            .Equal("Director", "Sound Editor", "Writer");
    }

    [Fact]
    public async Task Movie_WithoutVersion_KeepsTheInfoResponseShape()
    {
        using JsonDocument doc = await GetJson(MovieClient(), $"/api/v1/movie/{MovieId}");

        AssertInfoShape(doc, "Test Film");
    }

    [Fact]
    public async Task Tv_WithoutVersion_KeepsTheInfoResponseShape()
    {
        using JsonDocument doc = await GetJson(TvClient(), $"/api/v1/tv/{TvId}");

        AssertInfoShape(doc, "Test Show");
        doc.RootElement.GetProperty("data").GetProperty("seasons").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Collection_WithoutVersion_KeepsTheCollectionResponseShape()
    {
        using JsonDocument doc = await GetJson(
            CollectionClient(),
            $"/api/v1/collection/{CollectionId}"
        );

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("nextId", "data");
        JsonElement data = doc.RootElement.GetProperty("data");
        data.GetProperty("title").GetString().Should().Be("Test Saga");
        data.GetProperty("collection").GetArrayLength().Should().Be(3);
        data.GetProperty("cast").GetArrayLength().Should().BeGreaterThan(0);
    }

    private static void AssertInfoShape(JsonDocument doc, string title)
    {
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("data");
        JsonElement data = doc.RootElement.GetProperty("data");
        data.GetProperty("title").GetString().Should().Be(title);
        data.TryGetProperty("component", out _).Should().BeFalse();
        data.GetProperty("writer").GetProperty("name").GetString().Should().Be("Wim Writer");
        data.GetProperty("director").GetProperty("name").GetString().Should().Be("Dora Director");
        data.GetProperty("cast").GetArrayLength().Should().Be(5);
        data.GetProperty("crew").GetArrayLength().Should().Be(3);
    }
}
