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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// The artwork the home hero leads with.
/// </summary>
/// <remarks>
/// The rule these prove is the whole point of the feature: a home hero writes the title itself,
/// so it wants the print without the title on it. Every case here is one branch of "which poster
/// did we get, and may something be drawn over it" — get that wrong and the hero either reads its
/// own name twice or has no name at all.
/// </remarks>
[Trait("Category", "Unit")]
public class HomeRepositoryHeroArtworkTests : IDisposable
{
    private const int MovieId = 129;
    private const int TvId = 1399;

    private readonly MediaContext _context;
    private readonly HomeRepository _repository;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _factoryConnection;

    public HomeRepositoryHeroArtworkTests()
    {
        _context = TestMediaContextFactory.CreateSeededContext();
        (_factory, _factoryConnection) = TestMediaContextFactory.CreateFactory();
        _repository = new(_context, _factory);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_PrefersTextlessPoster_OverTheLocalisedPrint()
    {
        AddMovieImage("poster", null, "/textless.jpg");
        AddMovieImage("poster", "nl", "/dutch.jpg");
        AddMovieImage("poster", "en", "/english.jpg");
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "nl");

        Assert.Equal("/textless.jpg", artwork.Poster);
        Assert.True(artwork.PosterIsTextless);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_TakesTheBestVotedTextlessPoster()
    {
        AddMovieImage("poster", null, "/meh.jpg", voteAverage: 3.1);
        AddMovieImage("poster", null, "/best.jpg", voteAverage: 8.4);
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "en");

        Assert.Equal("/best.jpg", artwork.Poster);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_FallsBackToTheLocalePrint_WhenNoTextlessPosterExists()
    {
        AddMovieImage("poster", "nl", "/dutch.jpg");
        AddMovieImage("poster", "en", "/english.jpg");
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "nl");

        Assert.Equal("/dutch.jpg", artwork.Poster);
        Assert.False(artwork.PosterIsTextless);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_FallsBackToEnglish_WhenTheLocaleHasNoPrint()
    {
        AddMovieImage("poster", "en", "/english.jpg");
        AddMovieImage("poster", "fr", "/french.jpg");
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "nl");

        Assert.Equal("/english.jpg", artwork.Poster);
        Assert.False(artwork.PosterIsTextless);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_ReturnsNoPoster_WhenTheTitleHasNoImagesAtAll()
    {
        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "en");

        Assert.Null(artwork.Poster);
        Assert.Null(artwork.Logo);

        // Nothing was proven textless, so nothing may be drawn over whatever the caller
        // falls back to. Reporting true here would print the title over a print that
        // already carries it.
        Assert.False(artwork.PosterIsTextless);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_PrefersTheLocaleLogo()
    {
        AddMovieImage("logo", "nl", "/dutch-logo.png");
        AddMovieImage("logo", "en", "/english-logo.png");
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "nl");

        Assert.Equal("/dutch-logo.png", artwork.Logo);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_FallsBackToTheEnglishLogo()
    {
        AddMovieImage("logo", "en", "/english-logo.png");
        AddMovieImage("logo", "ja", "/japanese-logo.png");
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "nl");

        Assert.Equal("/english-logo.png", artwork.Logo);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_ReturnsNoLogo_WhenNeitherTheLocaleNorEnglishHasOne()
    {
        AddMovieImage("logo", "ja", "/japanese-logo.png");
        AddMovieImage("poster", null, "/textless.jpg");
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "nl");

        // The surface draws its own title in this case, which is only correct because the
        // poster it is drawing over was proven to have none.
        Assert.Null(artwork.Logo);
        Assert.True(artwork.PosterIsTextless);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_ReadsShowImages_ForATvHero()
    {
        AddTvImage("poster", null, "/show-textless.jpg");
        AddTvImage("logo", "en", "/show-logo.png");
        AddMovieImage("poster", null, "/movie-textless.jpg");
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(TvId, "tv", "en");

        Assert.Equal("/show-textless.jpg", artwork.Poster);
        Assert.Equal("/show-logo.png", artwork.Logo);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_IgnoresAnotherTitlesImages()
    {
        AddTvImage("poster", null, "/show-textless.jpg");
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "en");

        Assert.Null(artwork.Poster);
    }

    [Fact]
    public async Task GetHeroArtworkAsync_IgnoresBackdrops()
    {
        AddMovieImage("backdrop", null, "/backdrop.jpg");
        await _context.SaveChangesAsync();

        HeroArtwork artwork = await _repository.GetHeroArtworkAsync(MovieId, "movie", "en");

        Assert.Null(artwork.Poster);
    }

    private void AddMovieImage(
        string type,
        string? iso6391,
        string filePath,
        double voteAverage = 5.0
    )
    {
        _context.Images.Add(
            new()
            {
                FilePath = filePath,
                Type = type,
                Iso6391 = iso6391,
                VoteAverage = voteAverage,
                AspectRatio = 0.667,
                MovieId = MovieId,
            }
        );
    }

    private void AddTvImage(string type, string? iso6391, string filePath, double voteAverage = 5.0)
    {
        _context.Images.Add(
            new()
            {
                FilePath = filePath,
                Type = type,
                Iso6391 = iso6391,
                VoteAverage = voteAverage,
                AspectRatio = 0.667,
                TvId = TvId,
            }
        );
    }

    public void Dispose()
    {
        _context.Dispose();
        _factoryConnection.Dispose();
        GC.SuppressFinalize(this);
    }
}
