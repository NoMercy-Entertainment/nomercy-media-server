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

using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Storage;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Certificate;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Security;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Providers.AcoustId;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Service;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Api.Infrastructure;

public class NoMercyApiFactory : WebApplicationFactory<Startup>
{
    private static readonly object DbLock = new();
    private static bool _dbInitialized;

    public NoMercyApiFactory()
    {
        lock (DbLock)
        {
            if (!_dbInitialized)
            {
                EnsureDirectoriesAndSeedDatabase();
                _dbInitialized = true;
            }
        }

        // Re-seed the process-wide ClaimsPrincipalExtensions user cache from the
        // database at the start of every test class. Worker/coordinator tests
        // Reset() that static; with serialized collections a later class would
        // otherwise inherit an owner-less cache and get spurious 403s.
        using MediaContext userCacheContext = new();
        UserCache.Current.InitializeAsync(userCacheContext).GetAwaiter().GetResult();
    }

    // Every existing test in this fixture authenticates via TestAuthHandler (a header-
    // driven fake scheme), which never exercises the real Keycloak JwtBearer pipeline
    // wired in ServiceConfiguration.Auth.cs. A subclass overrides this to true (and
    // ConfigureRealAuthentication) when a test needs to prove something about that real
    // pipeline itself — e.g. that an invalid/expired token is still rejected. Default
    // behavior for every other test is unchanged.
    protected virtual bool UseRealAuthentication => false;

    protected virtual void ConfigureRealAuthentication(IServiceCollection services) { }

    // Set by AnonymousNoMercyApiFactory: registers TestAnonymousAuthHandler (never
    // produces a principal) instead of TestAuthHandler (always authenticates unless
    // told to deny). Lets a test exercise the real anonymous request pipeline —
    // AccessLogMiddleware's [AllowAnonymous]-metadata gate in particular — which the
    // default, always-authenticated fixture cannot see a regression in.
    protected virtual bool UseAnonymousTestAuthentication => false;

    // Default plugin manager for tests that don't care about plugins. A subclass
    // overrides this to supply a manager with real/fake plugin instances (e.g. an auth
    // plugin, to prove OnTokenValidated can only ever add claims, never grant access).
    protected virtual IPluginManager CreateTestPluginManager() => new StubPluginManager();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            RemoveHostedServices(services);

            if (UseRealAuthentication)
                ConfigureRealAuthentication(services);
            else if (UseAnonymousTestAuthentication)
                ReplaceAuthWithNoDefaultPrincipal(services);
            else
                ReplaceAuth(services);

            ReplacePluginManager(services);
            ReplaceSetupState(services);

            // Ensure all ConnectedClients registrations (from Startup AND from
            // CreateWebHostBuilder.ConfigureServices) are replaced by the single
            // shared instance so controllers and tests operate on the same dictionary.
            services.RemoveAll<ConnectedClients>();
            services.AddSingleton(SharedConnectedClients);

            // ServerController depends on IAudioFingerprinter. The real
            // ChromaprintFingerprinter needs the native fpcalc/chromaprint
            // library, which the test host doesn't provide, so register a
            // no-op stand-in — the dashboard endpoints under test only need
            // the controller to construct, not to fingerprint audio.
            services.RemoveAll<IAudioFingerprinter>();
            services.AddTransient<IAudioFingerprinter>(_ => Mock.Of<IAudioFingerprinter>());

            // RecommendationsController's /{type}/{id} detail route calls straight through
            // to the real TMDB HTTP client for movie/tv metadata. Tests must never reach
            // the network, so replace both providers with loose mocks — Moq returns a
            // completed Task<null> for unconfigured Task<T?> members, which exercises the
            // controller's real "detail not found" 404 path deterministically.
            services.RemoveAll<IMovieMetadataProvider>();
            services.AddScoped<IMovieMetadataProvider>(_ => Mock.Of<IMovieMetadataProvider>());
            services.RemoveAll<ITvShowMetadataProvider>();
            services.AddScoped<ITvShowMetadataProvider>(_ => Mock.Of<ITvShowMetadataProvider>());
        });
    }

    protected override IWebHostBuilder? CreateWebHostBuilder()
    {
#pragma warning disable ASPDEPR008 // WebHost kept until Startup is migrated to minimal-hosting
        // Capture the instance into a local so the lambda below closes over it — the
        // lambda must not reference `this` because the lambda outlives the factory in
        // some test harness configurations.
        ConnectedClients sharedClients = SharedConnectedClients;

        return WebHost
            .CreateDefaultBuilder([])
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseStartup<Startup>()
            .ConfigureServices(services =>
            {
                services.AddSingleton(new StartupOptions());
                // Startup's constructor takes IApiVersionDescriptionProvider, which
                // must exist in the host services before ConfigureServices runs.
                // Asp.Versioning 10 made DefaultApiVersionDescriptionProvider
                // internal (and dropped ISunsetPolicyManager entirely), so a stub
                // with no descriptions stands in — swagger doc generation is not
                // under test here.
                services.AddSingleton<IApiVersionDescriptionProvider>(
                    new EmptyApiVersionDescriptionProvider()
                );

                // CustomLogger<T> depends on NoMercyLoggerProvider (which needs
                // NoMercyLoggerOptions); production wires all three in WebHostFactory.
                // The test host registers the logger itself, so it must register the
                // provider + options too, or activating any ILogger<T> throws.
                // LogDirectory defaults to null here, so tests write no log files.
                services.AddSingleton(new NmSystem.Logging.NoMercyLoggerOptions());
                services.AddSingleton<NmSystem.Logging.NoMercyLoggerProvider>();
                services.AddSingleton(typeof(ILogger<>), typeof(CustomLogger<>));

                // Register the shared ConnectedClients instance early.  Because
                // ConfigureServices callbacks are applied in registration order,
                // this runs before Startup.ConfigureServices.  Startup's later
                // AddSingleton<ConnectedClients>() call adds a SECOND descriptor; the
                // last-registered wins for plain resolution — so we must remove the
                // Startup-registered one in ConfigureTestServices (below) to guarantee
                // our instance is used.
                services.AddSingleton(sharedClients);
            });
#pragma warning restore ASPDEPR008
    }

    // The ConnectedClients singleton injected into the server's DI container.
    // Configured via ConfigureWebHost so it is the exact same instance the request
    // pipeline (and controllers) receive.  Tests that need to simulate an "online"
    // device seed this dictionary directly.
    public ConnectedClients SharedConnectedClients { get; } = new();

    // Forces the test server to start (if not already started) and returns the
    // ConnectedClients singleton from the server's root service provider.
    // This is the same instance that controllers receive via constructor injection.
    public ConnectedClients GetConnectedClients() => SharedConnectedClients;

    public static readonly Ulid MovieLibraryId = Ulid.NewUlid();
    public static readonly Ulid TvLibraryId = Ulid.NewUlid();
    public static readonly Ulid MusicLibraryId = Ulid.NewUlid();
    public static readonly Ulid MovieFolderId = Ulid.NewUlid();
    public static readonly Ulid TvFolderId = Ulid.NewUlid();
    public static readonly Ulid MusicFolderId = Ulid.NewUlid();

    /// <summary>
    /// A local driver with an empty rootPath — the shape production actually
    /// seeds for the system-local driver. The fixture's own system-local row
    /// carries a real root so the seeded folders resolve, so an unscoped driver
    /// needs a row of its own.
    /// </summary>
    public static readonly Ulid UnscopedLocalDriverId = Ulid.NewUlid();

    public static readonly Guid ArtistId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AlbumId1 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid TrackId1 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid TrackId2 = Guid.Parse("33333333-3333-3333-3333-333333333334");
    public static readonly Guid PlaylistId1 = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid MusicGenreId1 = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static readonly int FavoriteCollectionId = 900001;
    public static readonly Ulid FavoriteSpecialId = Ulid.NewUlid();

    private static void EnsureDirectoriesAndSeedDatabase()
    {
        foreach (string path in AppFiles.AllPaths())
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        // Initialize DataProtection + TokenStore before any AppDbContext access.
        // AppDbContext uses TokenStore value converters on the Configuration table,
        // so the protector must exist before EnsureCreated() or any query runs.
        ServiceCollection tokenServices = new();
        tokenServices
            .AddDataProtection()
            .PersistKeysToFileSystem(new(AppFiles.DataProtectionKeysDir))
            .SetApplicationName("NoMercyMediaServer");
        ServiceProvider tokenProvider = tokenServices.BuildServiceProvider();
        TokenStore.Initialize(tokenProvider);

        // Create app.db for AppDbContext (Configuration table, SecureValue columns).
        // Use EnsureCreated rather than delete+recreate — parallel test assembly runs
        // share the same NoMercy_test path and file deletion races cause lock errors.
        using AppDbContext appContext = new();
        appContext.Database.EnsureCreated();

        string mediaDbPath = Path.Combine(AppFiles.DataPath, "media.db");
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            string file = mediaDbPath + suffix;
            if (File.Exists(file))
                File.Delete(file);
        }

        using MediaContext mediaContext = new();
        mediaContext.Database.EnsureCreated();

        if (!mediaContext.Users.Any())
        {
            User testUser = new()
            {
                Id = TestAuthHandler.DefaultUserId,
                Email = TestAuthHandler.DefaultUserEmail,
                Name = TestAuthHandler.DefaultUserName,
                Owner = true,
                Allowed = true,
                Manage = true,
            };
            // A second, unrelated-but-allowed identity — impersonated via
            // HttpClientAuthExtensions.AsSecondaryUser() — so ownership-isolation
            // tests (e.g. UserPlaylistsControllerTests) exercise a real 404 from
            // the endpoint's own ownership check, not a 403 from MediaAccess.
            User secondaryTestUser = new()
            {
                Id = TestAuthHandler.SecondaryUserId,
                Email = TestAuthHandler.SecondaryUserEmail,
                Name = TestAuthHandler.SecondaryUserName,
                Owner = false,
                Allowed = true,
                Manage = false,
            };
            mediaContext.Users.AddRange(testUser, secondaryTestUser);
            mediaContext.SaveChanges();
        }

        SeedMediaData(mediaContext);

        UserCache.Current.InitializeAsync(mediaContext).GetAwaiter().GetResult();

        string queueDbPath = Path.Combine(AppFiles.DataPath, "queue.db");
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            string file = queueDbPath + suffix;
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Another parallel test process may hold the file; EnsureCreated will
                    // use the existing DB, which is acceptable for queue (read-only in tests).
                }
            }
        }

        using QueueContext queueContext = new();
        queueContext.Database.EnsureCreated();
    }

    private static void SeedMediaData(MediaContext context)
    {
        if (context.Libraries.Any())
            return;

        // Step 1: Core entities (no FK dependencies)
        Library movieLibrary = new()
        {
            Id = MovieLibraryId,
            Title = "Movies",
            Type = "movie",
            Order = 1,
        };
        Library tvLibrary = new()
        {
            Id = TvLibraryId,
            Title = "TV Shows",
            Type = "tv",
            Order = 2,
        };
        context.Libraries.AddRange(movieLibrary, tvLibrary);

        Driver systemLocalDriver = new()
        {
            Id = Driver.SystemLocalDriverId,
            Name = "Local Filesystem",
            Type = "local",
            Config = """{"rootPath":"/"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Driver unscopedLocalDriver = new()
        {
            Id = UnscopedLocalDriverId,
            Name = "Unscoped Local",
            Type = "local",
            Config = """{"rootPath":""}""",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        context.Drivers.AddRange(systemLocalDriver, unscopedLocalDriver);

        Folder movieFolder = new()
        {
            Id = MovieFolderId,
            Path = "/media/movies",
            DriverId = Driver.SystemLocalDriverId,
        };
        Folder tvFolder = new()
        {
            Id = TvFolderId,
            Path = "/media/tv",
            DriverId = Driver.SystemLocalDriverId,
        };
        context.Folders.AddRange(movieFolder, tvFolder);

        Genre actionGenre = new() { Id = 28, Name = "Action" };
        Genre dramaGenre = new() { Id = 18, Name = "Drama" };
        context.Genres.AddRange(actionGenre, dramaGenre);

        context.SaveChanges();

        // Step 2: Entities with FK to libraries/folders/user
        context.LibraryUser.AddRange(
            new LibraryUser(MovieLibraryId, TestAuthHandler.DefaultUserId),
            new LibraryUser(TvLibraryId, TestAuthHandler.DefaultUserId)
        );

        context.FolderLibrary.AddRange(
            new(MovieFolderId, MovieLibraryId),
            new(TvFolderId, TvLibraryId)
        );

        Movie movie1 = new()
        {
            Id = 129,
            Title = "Spirited Away",
            TitleSort = "spirited away",
            Overview =
                "A young girl, Chihiro, becomes trapped in a strange new world of spirits. When her parents undergo a mysterious transformation, she must call upon the courage she never knew she had to free her family.",
            Poster = "/39wmItIWsg5sZMyRUHLkWBcuVCM.jpg",
            Backdrop = "/Ab8mkHmkYADjU7wQiOkia9BzGvS.jpg",
            ReleaseDate = new DateTime(2001, 7, 20),
            LibraryId = MovieLibraryId,
            VoteAverage = 8.5,
        };
        Movie movie2 = new()
        {
            Id = 680,
            Title = "Pulp Fiction",
            TitleSort = "pulp fiction",
            Overview =
                "The lives of two mob hitmen intertwine in four tales of violence and redemption.",
            Poster = "/d5iIlFn5s0ImszYzBPb8JPIfbXD.jpg",
            Backdrop = "/suaEOtk1N1sgg2MTM7oZd2cfVp3.jpg",
            ReleaseDate = new DateTime(1994, 9, 10),
            LibraryId = MovieLibraryId,
            VoteAverage = 8.5,
        };
        context.Movies.AddRange(movie1, movie2);

        Tv show1 = new()
        {
            Id = 1399,
            Title = "Breaking Bad",
            TitleSort = "breaking bad",
            Overview =
                "A chemistry teacher teams up with a former student to cook and sell crystal meth.",
            Poster = "/ggFHVNu6YYI5L9pCfOacjizRGt.jpg",
            Backdrop = "/tsRy63Mu5cu8etL1X7ZLyf7UP1M.jpg",
            FirstAirDate = new DateTime(2008, 1, 20),
            NumberOfEpisodes = 62,
            NumberOfSeasons = 5,
            LibraryId = TvLibraryId,
            VoteAverage = 8.9,
        };
        context.Tvs.Add(show1);

        context.SaveChanges();

        // Step 3: Join tables and child entities (FK to movies/tv/genres)
        context.LibraryMovie.AddRange(
            new LibraryMovie(MovieLibraryId, 129),
            new LibraryMovie(MovieLibraryId, 680)
        );

        context.LibraryTv.Add(new(TvLibraryId, 1399));

        context.GenreMovie.AddRange(
            new GenreMovie { GenreId = 28, MovieId = 129 },
            new GenreMovie { GenreId = 18, MovieId = 129 },
            new GenreMovie { GenreId = 18, MovieId = 680 }
        );

        context.GenreTv.Add(new() { GenreId = 18, TvId = 1399 });

        Season season1 = new()
        {
            Id = 3572,
            Title = "Season 1",
            SeasonNumber = 1,
            EpisodeCount = 7,
            TvId = 1399,
        };
        context.Seasons.Add(season1);

        context.SaveChanges();

        // Step 4: Episodes (FK to season/tv) and video files (FK to movie/episode)
        Episode episode1 = new()
        {
            Id = 62085,
            Title = "Pilot",
            EpisodeNumber = 1,
            SeasonNumber = 1,
            TvId = 1399,
            SeasonId = 3572,
            Overview = "Walter White is diagnosed with advanced lung cancer.",
        };
        Episode episode2 = new()
        {
            Id = 62086,
            Title = "Cat's in the Bag...",
            EpisodeNumber = 2,
            SeasonNumber = 1,
            TvId = 1399,
            SeasonId = 3572,
            Overview = "Walt and Jesse deal with a corpse and a prisoner.",
        };
        context.Episodes.AddRange(episode1, episode2);

        VideoFile movieVideoFile1 = new()
        {
            Id = Ulid.NewUlid(),
            Filename = "Spirited.Away.2001.1080p.mkv",
            Folder = "/media/movies/Spirited Away (2001)",
            HostFolder = "/media/movies/Spirited Away (2001)",
            Languages = "[\"en\"]",
            Quality = "1080p",
            Share = MovieFolderId.ToString(),
            MovieId = 129,
        };
        VideoFile movieVideoFile2 = new()
        {
            Id = Ulid.NewUlid(),
            Filename = "Pulp.Fiction.1994.1080p.mkv",
            Folder = "/media/movies/Pulp Fiction (1994)",
            HostFolder = "/media/movies/Pulp Fiction (1994)",
            Languages = "[\"en\"]",
            Quality = "1080p",
            Share = MovieFolderId.ToString(),
            MovieId = 680,
        };
        context.VideoFiles.AddRange(movieVideoFile1, movieVideoFile2);

        context.SaveChanges();

        // Step 5: TV video files (FK to episodes)
        VideoFile tvVideoFile1 = new()
        {
            Id = Ulid.NewUlid(),
            Filename = "Breaking.Bad.S01E01.mkv",
            Folder = "/media/tv/Breaking Bad/Season 01",
            HostFolder = "/media/tv/Breaking Bad/Season 01",
            Languages = "[\"en\"]",
            Quality = "1080p",
            Share = TvFolderId.ToString(),
            EpisodeId = 62085,
        };
        VideoFile tvVideoFile2 = new()
        {
            Id = Ulid.NewUlid(),
            Filename = "Breaking.Bad.S01E02.mkv",
            Folder = "/media/tv/Breaking Bad/Season 01",
            HostFolder = "/media/tv/Breaking Bad/Season 01",
            Languages = "[\"en\"]",
            Quality = "1080p",
            Share = TvFolderId.ToString(),
            EpisodeId = 62086,
        };
        context.VideoFiles.AddRange(tvVideoFile1, tvVideoFile2);

        context.SaveChanges();

        // Step 6: Music entities — library, folder, artist, album, tracks, playlist, genre
        Library musicLibrary = new()
        {
            Id = MusicLibraryId,
            Title = "Music",
            Type = "music",
            Order = 3,
        };
        context.Libraries.Add(musicLibrary);

        Folder musicFolder = new()
        {
            Id = MusicFolderId,
            Path = "/media/music",
            DriverId = Driver.SystemLocalDriverId,
        };
        context.Folders.Add(musicFolder);

        context.SaveChanges();

        context.LibraryUser.Add(new(MusicLibraryId, TestAuthHandler.DefaultUserId));
        context.FolderLibrary.Add(new(MusicFolderId, MusicLibraryId));

        MusicGenre rockGenre = new() { Id = MusicGenreId1, Name = "Rock" };
        context.MusicGenres.Add(rockGenre);

        Artist artist1 = new()
        {
            Id = ArtistId1,
            Name = "Test Artist",
            TitleSort = "test artist",
            Description = "A test artist for snapshot testing",
            Cover = "/test-artist.jpg",
            HostFolder = "/media/music/Test Artist",
            LibraryId = MusicLibraryId,
            FolderId = MusicFolderId,
        };
        context.Artists.Add(artist1);

        Album album1 = new()
        {
            Id = AlbumId1,
            Name = "Test Album",
            Description = "A test album",
            Cover = "/test-album.jpg",
            Year = 2020,
            Tracks = 2,
            HostFolder = "/media/music/Test Artist/Test Album",
            LibraryId = MusicLibraryId,
            FolderId = MusicFolderId,
            LibraryFolder = null!,
        };
        context.Albums.Add(album1);

        context.SaveChanges();

        Track track1 = new()
        {
            Id = TrackId1,
            Name = "Test Track 1",
            TrackNumber = 1,
            DiscNumber = 1,
            Duration = "3:45",
            Filename = "01-test-track-1.flac",
            Folder = "/media/music/Test Artist/Test Album",
            HostFolder = "/media/music/Test Artist/Test Album",
            FolderId = MusicFolderId,
        };
        Track track2 = new()
        {
            Id = TrackId2,
            Name = "Test Track 2",
            TrackNumber = 2,
            DiscNumber = 1,
            Duration = "4:20",
            Filename = "02-test-track-2.flac",
            Folder = "/media/music/Test Artist/Test Album",
            HostFolder = "/media/music/Test Artist/Test Album",
            FolderId = MusicFolderId,
        };
        context.Tracks.AddRange(track1, track2);

        context.SaveChanges();

        // Step 7: Music join tables
        context.ArtistTrack.AddRange(
            new ArtistTrack { ArtistId = ArtistId1, TrackId = TrackId1 },
            new ArtistTrack { ArtistId = ArtistId1, TrackId = TrackId2 }
        );

        context.AlbumTrack.AddRange(
            new AlbumTrack { AlbumId = AlbumId1, TrackId = TrackId1 },
            new AlbumTrack { AlbumId = AlbumId1, TrackId = TrackId2 }
        );

        context.AlbumArtist.Add(new() { AlbumId = AlbumId1, ArtistId = ArtistId1 });

        context.ArtistLibrary.Add(new(ArtistId1, MusicLibraryId));
        context.AlbumLibrary.Add(new(AlbumId1, MusicLibraryId));

        context.LibraryTrack.AddRange(
            new LibraryTrack { LibraryId = MusicLibraryId, TrackId = TrackId1 },
            new LibraryTrack { LibraryId = MusicLibraryId, TrackId = TrackId2 }
        );

        context.ArtistMusicGenre.Add(new() { ArtistId = ArtistId1, MusicGenreId = MusicGenreId1 });

        Playlist playlist1 = new()
        {
            Id = PlaylistId1,
            Name = "Test Playlist",
            Description = "A test playlist",
            UserId = TestAuthHandler.DefaultUserId,
        };
        context.Playlists.Add(playlist1);

        context.SaveChanges();

        context.PlaylistTrack.Add(new() { PlaylistId = PlaylistId1, TrackId = TrackId1 });

        // Favorite the artist/track so favorites endpoints have data
        context.ArtistUser.Add(
            new() { ArtistId = ArtistId1, UserId = TestAuthHandler.DefaultUserId }
        );
        context.TrackUser.Add(new() { TrackId = TrackId1, UserId = TestAuthHandler.DefaultUserId });

        context.SaveChanges();

        // Step 8: A collection and a special so GET /userData/favorites has all four
        // video media types (movie, tv, collection, special) to browse, not just movie/tv.
        Collection favoriteCollection = new()
        {
            Id = FavoriteCollectionId,
            Title = "Test Collection",
            TitleSort = "test collection",
            LibraryId = MovieLibraryId,
            Parts = 1,
        };
        context.Collections.Add(favoriteCollection);

        Special favoriteSpecial = new() { Id = FavoriteSpecialId, Title = "Test Special" };
        context.Specials.Add(favoriteSpecial);

        context.SaveChanges();

        context.CollectionMovie.Add(new(FavoriteCollectionId, movie1.Id));
        context.SpecialItems.Add(new() { SpecialId = FavoriteSpecialId, MovieId = movie1.Id });

        context.SaveChanges();

        // Favorite movie 129, tv 1399, the collection and the special so
        // GET /userData/favorites has one of each type to browse.
        context.MovieUser.Add(new(movie1.Id, TestAuthHandler.DefaultUserId));
        context.TvUser.Add(new(show1.Id, TestAuthHandler.DefaultUserId));
        context.CollectionUser.Add(new(FavoriteCollectionId, TestAuthHandler.DefaultUserId));
        context.SpecialUser.Add(new(FavoriteSpecialId, TestAuthHandler.DefaultUserId));

        context.SaveChanges();
    }

    private static void RemoveHostedServices(IServiceCollection services)
    {
        List<ServiceDescriptor> hostedServices =
        [
            .. services.Where(d => d.ServiceType == typeof(IHostedService)),
        ];

        foreach (ServiceDescriptor descriptor in hostedServices)
            services.Remove(descriptor);
    }

    private static void ReplaceSetupState(IServiceCollection services)
    {
        services.RemoveAll<SetupState>();
        SetupState completedState = new();
        completedState.DetermineInitialPhase(hasValidToken: true);
        services.AddSingleton(completedState);

        // AuthManager is registered as a singleton that creates its own AppDbContext scope.
        // In tests, app.db is already seeded — provide a standalone instance with its own
        // AppDbContext so the real DI scope factory pattern doesn't run and hit a missing DB.
        services.RemoveAll<AuthManager>();
        services.RemoveAll<SetupEndpoints>();
        services.RemoveAll<BootOrchestrator>();

        AppDbContext testAppContext = new();
        testAppContext.Database.EnsureCreated();

        AuthManager testAuthManager = new(
            testAppContext,
            new LocalStorageDriver(),
            new AuthTokenStore()
        );
        services.AddSingleton(testAuthManager);
        IServerRegistrationService registrationService = Mock.Of<IServerRegistrationService>();
        IHttpClientFactory httpClientFactory = Mock.Of<IHttpClientFactory>();
        IHostApplicationLifetime appLifetime = Mock.Of<IHostApplicationLifetime>();
        services.AddSingleton(
            new SetupEndpoints(
                completedState,
                testAuthManager,
                registrationService,
                httpClientFactory,
                appLifetime
            )
        );
        services.AddSingleton(
            new BootOrchestrator(
                completedState,
                testAuthManager,
                Mock.Of<IApiKeyLoader>(),
                Mock.Of<IDegradedModeRecovery>(),
                registrationService,
                new AuthTokenStore(),
                new CertificateService(NullLogger<CertificateService>.Instance, null!),
                httpClientFactory
            )
        );
    }

    private void ReplacePluginManager(IServiceCollection services)
    {
        services.RemoveAll<IPluginManager>();
        services.AddSingleton(CreateTestPluginManager());

        // Every real server has an owner, and plugin access is answered
        // against one. A host with none refuses every caller, which would make
        // these tests measure an installation nobody runs.
        services.RemoveAll<IPluginOwner>();
        services.AddSingleton<IPluginOwner>(new TestPluginOwner());
    }

    private sealed class TestPluginOwner : IPluginOwner
    {
        public Guid Id => TestAuthHandler.DefaultUserId;
    }

    private static void ReplaceAuth(IServiceCollection services)
    {
        services.RemoveAll<IAuthenticationSchemeProvider>();
        services.RemoveAll<IAuthenticationHandlerProvider>();

        services
            .AddAuthentication(TestAuthDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthDefaults.AuthenticationScheme,
                _ => { }
            );

        services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(
                new AuthorizationPolicyBuilder(TestAuthDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build()
            )
            .AddPolicy(
                "api",
                new AuthorizationPolicyBuilder(TestAuthDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build()
            );
    }

    // Same authorization policies as ReplaceAuth (default policy + "api" both
    // require an authenticated user), but the scheme is TestAnonymousAuthHandler,
    // which never produces one. A request with no special header therefore behaves
    // exactly like a real anonymous caller: rejected by UseAuthorization for any
    // [Authorize]-protected endpoint, waved through for any [AllowAnonymous] one.
    private static void ReplaceAuthWithNoDefaultPrincipal(IServiceCollection services)
    {
        services.RemoveAll<IAuthenticationSchemeProvider>();
        services.RemoveAll<IAuthenticationHandlerProvider>();

        services
            .AddAuthentication(TestAuthDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAnonymousAuthHandler>(
                TestAuthDefaults.AuthenticationScheme,
                _ => { }
            );

        services
            .AddAuthorizationBuilder()
            .SetDefaultPolicy(
                new AuthorizationPolicyBuilder(TestAuthDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build()
            )
            .AddPolicy(
                "api",
                new AuthorizationPolicyBuilder(TestAuthDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build()
            );
    }

    private sealed class EmptyApiVersionDescriptionProvider : IApiVersionDescriptionProvider
    {
        public IReadOnlyList<ApiVersionDescription> ApiVersionDescriptions { get; } = [];
    }

    private sealed class StubPluginManager : IPluginManager
    {
        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [];

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => [];
    }
}

/// <summary>
/// Same seeded database and DI wiring as <see cref="NoMercyApiFactory"/>, but the
/// default HTTP client carries NO principal at all — see
/// <see cref="TestAnonymousAuthHandler"/>. Every other fixture in this test suite
/// authenticates by default (TestAuthHandler), which never exercises the real
/// anonymous-request path through AccessLogMiddleware; a test that needs to prove
/// an [AllowAnonymous] route actually answers without a bearer token uses this
/// factory instead.
/// </summary>
public class AnonymousNoMercyApiFactory : NoMercyApiFactory
{
    protected override bool UseAnonymousTestAuthentication => true;
}
