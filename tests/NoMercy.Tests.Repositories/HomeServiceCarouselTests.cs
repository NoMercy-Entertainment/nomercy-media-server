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
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.Services;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

// GetHomeData emits the per-library "Latest in {library}" carousels (a desktop
// home surface) between continue-watching and the genre carousels. These pin
// that the rows are present AND that the circular nav chain
// (continue <-> library_* <-> genre_* <-> continue) only ever points at ids
// that actually exist in the response.
public class HomeServiceCarouselTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<MediaContext> _factory;

    public HomeServiceCarouselTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateFactory();

        using MediaContext seedContext = _factory.CreateDbContext();
        TestMediaContextFactory.SeedData(seedContext);
    }

    private async Task<List<ComponentEnvelope>> GetHomeComponentsAsync()
    {
        await using MediaContext mainContext = await _factory.CreateDbContextAsync();
        HomeRepository homeRepository = new(mainContext, _factory);
        LibraryRepository libraryRepository = new(_factory);
        HomeService service = new(homeRepository, libraryRepository, _factory);

        ComponentResponse response = await service.GetHomeData(SeedConstants.UserId, "en", "US");

        return [.. response.Data];
    }

    [Fact]
    public async Task GetHomeData_EmitsLibraryCarousels()
    {
        List<ComponentEnvelope> components = await GetHomeComponentsAsync();

        Assert.Contains(
            components,
            envelope =>
                envelope.Component == ComponentTypes.Carousel
                && GetComponentId(envelope)?.StartsWith("library_") == true
        );

        Assert.Contains(
            components,
            envelope => GetComponentTitle(envelope)?.StartsWith("Latest in") == true
        );
    }

    [Fact]
    public async Task GetHomeData_StillEmitsHomeCardContinueAndGenreCarousels()
    {
        List<ComponentEnvelope> components = await GetHomeComponentsAsync();

        Assert.Contains(components, envelope => envelope.Component == ComponentTypes.HomeCard);

        Assert.Contains(
            components,
            envelope =>
                envelope.Component == ComponentTypes.Carousel
                && GetComponentId(envelope) == "continue"
        );

        Assert.Contains(
            components,
            envelope =>
                envelope.Component == ComponentTypes.Carousel
                && GetComponentId(envelope)?.StartsWith("genre_") == true
        );
    }

    [Fact]
    public async Task GetHomeData_NavigationChainOnlyReferencesExistingComponents()
    {
        List<ComponentEnvelope> components = await GetHomeComponentsAsync();

        HashSet<string> existingCarouselIds =
        [
            .. components
                .Where(envelope => envelope.Component == ComponentTypes.Carousel)
                .Select(GetComponentId)
                .Where(id => id != null)
                .Select(id => id!),
        ];

        // Sanity: the seed data produces at least the continue + genre rows this
        // test is meant to walk, otherwise the loop below would vacuously pass.
        Assert.True(existingCarouselIds.Count >= 2);

        foreach (ComponentEnvelope envelope in components)
        {
            if (envelope.Props is not ContainerProps containerProps)
                continue;

            if (containerProps.PreviousId is string previousId)
                Assert.Contains(previousId, existingCarouselIds);

            if (containerProps.NextId is string nextId)
                Assert.Contains(nextId, existingCarouselIds);
        }
    }

    private static string? GetComponentId(ComponentEnvelope envelope)
    {
        return envelope.Props switch
        {
            ContainerProps containerProps => containerProps.Id as string,
            _ => null,
        };
    }

    private static string? GetComponentTitle(ComponentEnvelope envelope)
    {
        return envelope.Props switch
        {
            ContainerProps containerProps => containerProps.Title,
            _ => null,
        };
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
