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

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NoMercy.Database.Models.Encoder;
using NoMercy.Database.Models.Security;
using NoMercy.Database.Models.Storage;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;

namespace NoMercy.Database;

public class MediaContext : DbContext
{
    public MediaContext(DbContextOptions<MediaContext> options)
        : base(options)
    {
        //
    }

    public MediaContext() { }

    // Backs the adult-content query filter. Must be an instance member: EF Core
    // parameterizes instance references in a filter (re-read per query) but
    // constant-folds a static reference into the cached model, which would
    // freeze the value at first build and ignore a runtime toggle.
    public bool ShowAdultContent => RuntimeServerSettings.Current.ShowAdultContent;

    [DbFunction("normalize_search", IsBuiltIn = true)]
    public static string NormalizeSearch(string? input) =>
        throw new NotSupportedException("This method is for EF Core query translation only.");

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (!options.IsConfigured)
            options.UseSqlite(
                $"Data Source={AppFiles.MediaDatabase}; Pooling=True; Foreign Keys=True; Default Timeout=30;",
                o =>
                {
                    o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                    o.ExecutionStrategy(deps => new SqliteRetryingExecutionStrategy(deps));
                }
            );

        if (Config.IsDev)
            options.EnableSensitiveDataLogging();

        options.AddInterceptors([
            new EntityBaseUpdatedAtInterceptor(),
            new SqliteNormalizeSearchInterceptor(),
            new SqliteConnectionInterceptor(),
        ]);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<string>().HaveMaxLength(256);

        configurationBuilder.Properties<Ulid>().HaveConversion<UlidToStringConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(
            typeof(MediaContext).GetMethod(nameof(NormalizeSearch), [typeof(string)])!
        );

        modelBuilder
            .Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties())
            .Where(p => p.Name is "CreatedAt" or "UpdatedAt")
            .ToList()
            .ForEach(p => p.SetDefaultValueSql("CURRENT_TIMESTAMP"));

        // Default to Restrict to prevent accidental cascading deletes across the schema.
        // Relationships that genuinely need cascading (e.g. owned/dependent records) are
        // configured explicitly below with OnDelete(DeleteBehavior.Cascade).
        modelBuilder
            .Model.GetEntityTypes()
            .SelectMany(t => t.GetForeignKeys())
            .ToList()
            .ForEach(p => p.DeleteBehavior = DeleteBehavior.Restrict);

        modelBuilder.Entity<Cast>().Property(t => t.RoleId).IsRequired(false);

        modelBuilder.Entity<Crew>().Property(t => t.JobId).IsRequired(false);

        // Driver.Config is free-form JSON — no max-length cap.
        modelBuilder.Entity<Driver>().Property(d => d.Config).HasMaxLength(int.MaxValue);

        // Folder.DriverId FK — Restrict deletion if any folder references the driver.
        // The DriversController already returns 409 before reaching DELETE, so Restrict
        // is the correct DB-level enforcement: drivers cannot be deleted while in use.
        modelBuilder
            .Entity<Folder>()
            .HasOne(f => f.Driver)
            .WithMany(d => d.Folders)
            .HasForeignKey(f => f.DriverId)
            .OnDelete(DeleteBehavior.Restrict);

        // Metadata optionally designates one Track as its AudioTrack; it does not own that
        // Track. AudioTrackId is nullable, so deleting the Track clears the pointer instead
        // of destroying the Metadata row — Metadata legitimately survives without one.
        modelBuilder
            .Entity<Metadata>()
            .HasOne(m => m.AudioTrack)
            .WithOne()
            .HasForeignKey<Metadata>(m => m.AudioTrackId)
            .OnDelete(DeleteBehavior.SetNull);

        // Metadata is shared, reference-style data: many Tracks in the same folder can
        // point at the same Metadata row via MetadataId, so no single Track owns it (same
        // shape as VideoFile.MetadataId above, left at the file's default Restrict). Block
        // deleting a Metadata row while any Track still references it, rather than
        // cascading and wiping every other Track that shares it.
        modelBuilder
            .Entity<Track>()
            .HasOne(t => t.Metadata)
            .WithMany()
            .HasForeignKey(t => t.MetadataId)
            .OnDelete(DeleteBehavior.Restrict);

        // PlaylistItem is owned by its UserPlaylist (the video-only playlist
        // container — entirely separate from the music Playlist table): deleting a
        // user's playlist should remove its items, not orphan/block on them. No
        // inverse collection nav is declared on UserPlaylist, so this is configured
        // one-directionally via WithMany().
        // PlaylistItem's other FKs (Movie/Tv/Episode) already cascade automatically
        // via the cascadeParents rule further below; Special is left at the default
        // Restrict, matching the existing SpecialItem.SpecialId posture elsewhere in
        // this schema.
        modelBuilder
            .Entity<PlaylistItem>()
            .HasOne(pi => pi.UserPlaylist)
            .WithMany()
            .HasForeignKey(pi => pi.UserPlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<TrackAudioAnalysis>()
            .HasOne(a => a.Track)
            .WithOne(t => t.AudioAnalysis)
            .HasForeignKey<TrackAudioAnalysis>(a => a.TrackId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties())
            .Where(p => p.ClrType == typeof(string))
            .ToList()
            .ForEach(p =>
            {
                MaxLengthAttribute? maxLengthAttr =
                    p.PropertyInfo?.GetCustomAttribute<MaxLengthAttribute>();
                if (maxLengthAttr is not null)
                    p.SetMaxLength(maxLengthAttr.Length);
            });

        List<IMutableEntityType> entityTypes =
        [
            .. modelBuilder
                .Model.GetEntityTypes()
                .Where(t =>
                    t.ClrType.IsSubclassOf(typeof(Timestamps)) || t.ClrType == typeof(Timestamps)
                ),
        ];

        foreach (IMutableEntityType entityType in entityTypes)
        {
            string? tableName = entityType.GetTableName();
            modelBuilder
                .Entity(entityType.ClrType)
                .ToTable(tb => tb.HasTrigger($"update_{tableName}_updated_at"));
        }

        // Explicit cascade for direct entity → Library FKs. These use ConfigurationSource.Explicit
        // so they cannot be reset by convention re-processing (the mutation loop above only sets
        // ConventionSource and gets overridden when HasTrigger calls re-process those entities).
        modelBuilder
            .Entity<Movie>()
            .HasOne(m => m.Library)
            .WithMany()
            .HasForeignKey(m => m.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<Tv>()
            .HasOne(t => t.Library)
            .WithMany()
            .HasForeignKey(t => t.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<Collection>()
            .HasOne(c => c.Library)
            .WithMany()
            .HasForeignKey(c => c.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Base adult-content filter: entities carrying an Adult column never surface
        // explicit rows unless the server explicitly enables it. Filters reference the
        // instance ShowAdultContent so EF parameterizes the value per query, letting a
        // runtime toggle take effect on the next query. Internal by-id lookups in the
        // encode/file pipeline use IgnoreQueryFilters() so already present media still
        // processes.
        modelBuilder
            .Entity<Movie>()
            .HasQueryFilter(movie => ShowAdultContent || !movie.Adult);
        modelBuilder.Entity<Person>().HasQueryFilter(person => ShowAdultContent || !person.Adult);

        // Required dependents mirror their principal's filter. EF Core needs a matching
        // filter on the required end of a filtered relationship, otherwise a hidden adult
        // Movie/Person still surfaces its join rows (the linkid=2131316 warning).
        modelBuilder
            .Entity<CertificationMovie>()
            .HasQueryFilter(certificationMovie =>
                ShowAdultContent || !certificationMovie.Movie.Adult
            );
        modelBuilder
            .Entity<CollectionMovie>()
            .HasQueryFilter(collectionMovie => ShowAdultContent || !collectionMovie.Movie.Adult);
        modelBuilder
            .Entity<CompanyMovie>()
            .HasQueryFilter(companyMovie => ShowAdultContent || !companyMovie.Movie.Adult);
        modelBuilder
            .Entity<GenreMovie>()
            .HasQueryFilter(genreMovie => ShowAdultContent || !genreMovie.Movie.Adult);
        modelBuilder
            .Entity<AnimeThemeMovie>()
            .HasQueryFilter(animeThemeMovie => ShowAdultContent || !animeThemeMovie.Movie.Adult);
        modelBuilder
            .Entity<AnimeDemographicMovie>()
            .HasQueryFilter(animeDemographicMovie =>
                ShowAdultContent || !animeDemographicMovie.Movie.Adult
            );
        modelBuilder
            .Entity<AnimeSeasonMovie>()
            .HasQueryFilter(animeSeasonMovie => ShowAdultContent || !animeSeasonMovie.Movie.Adult);
        modelBuilder
            .Entity<KeywordMovie>()
            .HasQueryFilter(keywordMovie => ShowAdultContent || !keywordMovie.Movie.Adult);
        modelBuilder
            .Entity<LibraryMovie>()
            .HasQueryFilter(libraryMovie => ShowAdultContent || !libraryMovie.Movie.Adult);
        modelBuilder
            .Entity<MovieUser>()
            .HasQueryFilter(movieUser => ShowAdultContent || !movieUser.Movie.Adult);
        modelBuilder.Entity<Cast>().HasQueryFilter(cast => ShowAdultContent || !cast.Person.Adult);
        modelBuilder.Entity<Crew>().HasQueryFilter(crew => ShowAdultContent || !crew.Person.Adult);
        modelBuilder
            .Entity<Creator>()
            .HasQueryFilter(creator => ShowAdultContent || !creator.Person.Adult);
        modelBuilder
            .Entity<GuestStar>()
            .HasQueryFilter(guestStar => ShowAdultContent || !guestStar.Person.Adult);

        modelBuilder
            .Entity<Album>()
            .HasOne(a => a.Library)
            .WithMany()
            .HasForeignKey(a => a.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Content-ownership cascades: when a Movie/VideoFile is deleted, remove its owned records.
        // Use the correct WithMany() collection navigation to match the convention-discovered
        // relationship — otherwise EF Core creates a duplicate FK property (e.g. MovieId1).
        modelBuilder
            .Entity<GenreMovie>()
            .HasOne(gm => gm.Movie)
            .WithMany(m => m.GenreMovies)
            .HasForeignKey(gm => gm.MovieId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<AnimeThemeMovie>()
            .HasOne(atm => atm.AnimeTheme)
            .WithMany(t => t.AnimeThemeMovies)
            .HasForeignKey(atm => atm.AnimeThemeId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder
            .Entity<AnimeThemeTv>()
            .HasOne(att => att.AnimeTheme)
            .WithMany(t => t.AnimeThemeTvShows)
            .HasForeignKey(att => att.AnimeThemeId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder
            .Entity<AnimeDemographicMovie>()
            .HasOne(adm => adm.AnimeDemographic)
            .WithMany(d => d.AnimeDemographicMovies)
            .HasForeignKey(adm => adm.AnimeDemographicId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder
            .Entity<AnimeDemographicTv>()
            .HasOne(adt => adt.AnimeDemographic)
            .WithMany(d => d.AnimeDemographicTvShows)
            .HasForeignKey(adt => adt.AnimeDemographicId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder
            .Entity<AnimeSeasonMovie>()
            .HasOne(asm => asm.AnimeSeason)
            .WithMany(s => s.AnimeSeasonMovies)
            .HasForeignKey(asm => asm.AnimeSeasonId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder
            .Entity<AnimeSeasonTv>()
            .HasOne(ast => ast.AnimeSeason)
            .WithMany(s => s.AnimeSeasonTvShows)
            .HasForeignKey(ast => ast.AnimeSeasonId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<VideoFile>()
            .HasOne(v => v.Movie)
            .WithMany(m => m.VideoFiles)
            .HasForeignKey(v => v.MovieId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<UserData>()
            .HasOne(u => u.Movie)
            .WithMany(m => m.UserData)
            .HasForeignKey(u => u.MovieId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<UserData>()
            .HasOne(u => u.VideoFile)
            .WithMany(v => v.UserData)
            .HasForeignKey(u => u.VideoFileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ownership cascades: deleting a parent removes all dependent records.
        // Tv → (seasons, episodes, casts, images, translations, join tables, etc.)
        // Episode → (video files, casts, images, translations, etc.)
        // Season → (episodes inherit via Tv, but season-specific records need cascade)
        // Library → (join tables)
        // VideoFile → (user data, playback preferences)
        Type[] cascadeParents =
        [
            typeof(Library),
            typeof(Tv),
            typeof(Episode),
            typeof(Season),
            typeof(VideoFile),
            typeof(Movie),
        ];

        modelBuilder
            .Model.GetEntityTypes()
            .SelectMany(t => t.GetForeignKeys())
            .Where(fk => cascadeParents.Contains(fk.PrincipalEntityType.ClrType))
            .ToList()
            .ForEach(fk => fk.DeleteBehavior = DeleteBehavior.Cascade);

        // Server-orchestrated cast: device fingerprint uniqueness scoped to
        // owner; SET NULL on owner delete so devices survive but become unowned.
        modelBuilder
            .Entity<Device>()
            .HasIndex(d => new { d.OwnerUserId, d.Fingerprint })
            .IsUnique()
            .HasFilter("Fingerprint IS NOT NULL");

        modelBuilder
            .Entity<Device>()
            .HasOne(d => d.OwnerUser)
            .WithMany()
            .HasForeignKey(d => d.OwnerUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // A device-attributed entry is meaningless once the device is gone, so those still
        // cascade. The FK is optional now: system events — an encode, a scheduled scan — have
        // no device at all, and those rows must survive on their own.
        modelBuilder
            .Entity<ActivityLog>()
            .HasOne(al => al.Device)
            .WithMany(d => d.ActivityLogs)
            .HasForeignKey(al => al.DeviceId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<ActivityLog>()
            .HasOne(al => al.User)
            .WithMany()
            .HasForeignKey(al => al.UserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EncodingPresetFolder>(b =>
        {
            b.HasOne(epf => epf.Preset)
                .WithMany()
                .HasForeignKey(epf => epf.PresetId)
                .OnDelete(DeleteBehavior.Cascade);
            // Folder.EncodingPresetFolders is the inverse — without naming it
            // EF Core fabricates a shadow FK (FolderId1) for the new collection
            // navigation and queries fail with "no such column: e1.FolderId1".
            b.HasOne(epf => epf.Folder)
                .WithMany(f => f.EncodingPresetFolders)
                .HasForeignKey(epf => epf.FolderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // EncodeTaskOutcome.OutputArtifactsJson is free-form concatenated paths
        // and must exceed the global 256-char string cap.
        modelBuilder
            .Entity<EncodeTaskOutcome>()
            .Property(o => o.OutputArtifactsJson)
            .HasMaxLength(int.MaxValue);

        // EncodeTaskOutcome.ErrorMessage may hold detailed error text up to 4096 chars.
        modelBuilder.Entity<EncodeTaskOutcome>().Property(o => o.ErrorMessage).HasMaxLength(4096);

        ConfigureColorPaletteIndexes(modelBuilder);
        ConfigureImageForeignKeyIndexes(modelBuilder);
        ConfigureCreditForeignKeyIndexes(modelBuilder);
        ConfigurePlaylistItemForeignKeyIndexes(modelBuilder);

        modelBuilder.Entity<InboxItem>().Property(i => i.CandidatesJson).HasMaxLength(int.MaxValue);
        modelBuilder
            .Entity<InboxItem>()
            .Property(i => i.SelectedMatchJson)
            .HasMaxLength(int.MaxValue);
        modelBuilder.Entity<InboxItem>().Property(i => i.Error).HasMaxLength(4096);

        base.OnModelCreating(modelBuilder);
    }

    public virtual DbSet<Driver> Drivers { get; init; }
    public virtual DbSet<ActivityLog> ActivityLogs { get; init; }
    public virtual DbSet<Cast> Casts { get; init; }
    public virtual DbSet<CertificationMovie> CertificationMovie { get; init; }
    public virtual DbSet<CertificationTv> CertificationTv { get; init; }
    public virtual DbSet<Certification> Certifications { get; init; }
    public virtual DbSet<CollectionLibrary> CollectionLibrary { get; init; }
    public virtual DbSet<CollectionMovie> CollectionMovie { get; init; }
    public virtual DbSet<Collection> Collections { get; init; }
    public virtual DbSet<Country> Countries { get; init; }
    public virtual DbSet<Creator> Creators { get; init; }
    public virtual DbSet<Crew> Crews { get; init; }
    public virtual DbSet<Device> Devices { get; init; }
    public virtual DbSet<DeviceDropNotice> DeviceDropNotices { get; init; }
    public virtual DbSet<EncodingHistory> EncodingHistory { get; init; }
    public virtual DbSet<EncodingPreset> EncodingPresets { get; init; }
    public DbSet<EncodingPresetFolder> EncodingPresetFolders => Set<EncodingPresetFolder>();
    public virtual DbSet<ContentSegment> ContentSegments { get; init; }
    public virtual DbSet<Episode> Episodes { get; init; }
    public virtual DbSet<FolderLibrary> FolderLibrary { get; init; }
    public virtual DbSet<Folder> Folders { get; init; }
    public DbSet<IpBan> IpBans => Set<IpBan>();
    public virtual DbSet<GenreMovie> GenreMovie { get; init; }
    public virtual DbSet<GenreTv> GenreTv { get; init; }
    public virtual DbSet<Genre> Genres { get; init; }
    public virtual DbSet<AnimeTheme> AnimeThemes { get; init; }
    public virtual DbSet<AnimeThemeMovie> AnimeThemeMovie { get; init; }
    public virtual DbSet<AnimeThemeTv> AnimeThemeTv { get; init; }
    public virtual DbSet<AnimeDemographic> AnimeDemographics { get; init; }
    public virtual DbSet<AnimeDemographicMovie> AnimeDemographicMovie { get; init; }
    public virtual DbSet<AnimeDemographicTv> AnimeDemographicTv { get; init; }
    public virtual DbSet<AnimeSeason> AnimeSeasons { get; init; }
    public virtual DbSet<AnimeSeasonMovie> AnimeSeasonMovie { get; init; }
    public virtual DbSet<AnimeSeasonTv> AnimeSeasonTv { get; init; }
    public virtual DbSet<GuestStar> GuestStars { get; init; }
    public virtual DbSet<Image> Images { get; init; }
    public virtual DbSet<WatchProvider> WatchProviders { get; init; }
    public virtual DbSet<WatchProviderMedia> WatchProviderMedia { get; init; }
    public virtual DbSet<Network> Networks { get; init; }
    public virtual DbSet<NetworkTv> NetworkTv { get; init; }

    public virtual DbSet<Job> Jobs { get; init; }
    public virtual DbSet<KeywordMovie> KeywordMovie { get; init; }
    public virtual DbSet<KeywordTv> KeywordTv { get; init; }
    public virtual DbSet<Keyword> Keywords { get; init; }
    public virtual DbSet<LanguageLibrary> LanguageLibrary { get; init; }
    public virtual DbSet<Language> Languages { get; init; }
    public virtual DbSet<Library> Libraries { get; init; }
    public virtual DbSet<ImportFailure> ImportFailures { get; init; }
    public virtual DbSet<LibraryMovie> LibraryMovie { get; init; }
    public virtual DbSet<LibraryTv> LibraryTv { get; init; }
    public virtual DbSet<LibraryTrack> LibraryTrack { get; init; }
    public virtual DbSet<LibraryUser> LibraryUser { get; init; }
    public virtual DbSet<CollectionUser> CollectionUser { get; init; }
    public virtual DbSet<MovieUser> MovieUser { get; init; }
    public virtual DbSet<TvUser> TvUser { get; init; }
    public virtual DbSet<SpecialUser> SpecialUser { get; init; }
    public virtual DbSet<MediaAttachment> MediaAttachments { get; init; }
    public virtual DbSet<Media> Medias { get; init; }
    public virtual DbSet<MediaStream> MediaStreams { get; init; }
    public virtual DbSet<Message> Messages { get; init; }
    public virtual DbSet<Metadata> Metadata { get; init; }
    public virtual DbSet<Movie> Movies { get; init; }
    public virtual DbSet<MusicGenreTrack> MusicGenreTrack { get; init; }
    public virtual DbSet<MusicGenre> MusicGenres { get; init; }
    public virtual DbSet<NotificationUser> NotificationUser { get; init; }
    public virtual DbSet<Notification> Notifications { get; init; }
    public virtual DbSet<Person> People { get; init; }
    public virtual DbSet<Playlist> Playlists { get; init; }
    public virtual DbSet<PlaylistItem> PlaylistItems { get; init; }
    public virtual DbSet<UserPlaylist> UserPlaylists { get; init; }
    public virtual DbSet<Recommendation> Recommendations { get; init; }
    public virtual DbSet<Role> Roles { get; init; }
    public virtual DbSet<RunningTask> RunningTasks { get; init; }
    public virtual DbSet<Season> Seasons { get; init; }
    public virtual DbSet<Similar> Similar { get; init; }
    public virtual DbSet<SpecialItem> SpecialItems { get; init; }
    public virtual DbSet<Special> Specials { get; init; }
    public virtual DbSet<Translation> Translations { get; init; }
    public virtual DbSet<Tv> Tvs { get; init; }
    public virtual DbSet<UserData> UserData { get; init; }
    public virtual DbSet<User> Users { get; init; }
    public virtual DbSet<VideoFile> VideoFiles { get; init; }
    public virtual DbSet<Company> Companies { get; init; }
    public virtual DbSet<CompanyMovie> CompanyMovie { get; init; }
    public virtual DbSet<CompanyTv> CompanyTv { get; init; }

    public virtual DbSet<AlbumArtist> AlbumArtist { get; init; }
    public virtual DbSet<AlbumLibrary> AlbumLibrary { get; init; }
    public virtual DbSet<AlbumMusicGenre> AlbumMusicGenre { get; init; }
    public virtual DbSet<AlbumTrack> AlbumTrack { get; init; }
    public virtual DbSet<AlbumUser> AlbumUser { get; init; }
    public virtual DbSet<Album> Albums { get; init; }
    public virtual DbSet<AlternativeTitle> AlternativeTitles { get; init; }
    public virtual DbSet<ArtistLibrary> ArtistLibrary { get; init; }
    public virtual DbSet<ArtistMusicGenre> ArtistMusicGenre { get; init; }
    public virtual DbSet<ArtistTrack> ArtistTrack { get; init; }
    public virtual DbSet<ArtistUser> ArtistUser { get; init; }
    public virtual DbSet<Artist> Artists { get; init; }
    public virtual DbSet<MusicPlay> MusicPlays { get; init; }
    public virtual DbSet<PlaylistTrack> PlaylistTrack { get; init; }
    public virtual DbSet<TrackUser> TrackUser { get; init; }
    public virtual DbSet<Track> Tracks { get; init; }
    public virtual DbSet<TrackAudioAnalysis> TrackAudioAnalysis { get; init; }
    public virtual DbSet<ReleaseGroup> ReleaseGroups { get; init; }
    public virtual DbSet<AlbumReleaseGroup> AlbumReleaseGroup { get; init; }
    public virtual DbSet<ArtistReleaseGroup> ArtistReleaseGroup { get; init; }
    public virtual DbSet<MusicGenreReleaseGroup> MusicGenreReleaseGroup { get; init; }

    public virtual DbSet<PlaybackPreference> PlaybackPreferences { get; init; }
    public virtual DbSet<TrustedPublisherKey> TrustedPublisherKeys { get; init; }
    public virtual DbSet<EncodeTaskOutcome> EncodeTaskOutcomes { get; init; }
    public virtual DbSet<IncompleteEncode> IncompleteEncodes { get; init; }
    public virtual DbSet<InboxItem> InboxItems { get; init; }

    // PlaylistItem is polymorphic: exactly one of its four owner-FK columns is set per
    // row, so every column is NULL on at least 3/4 of the table (the same sparse-FK
    // shape as ConfigureImageForeignKeyIndexes/ConfigureCreditForeignKeyIndexes below —
    // a plain index over a mostly-NULL column is non-selective and SQLite falls back to
    // a full table scan). Filtering each index to its non-NULL rows keeps every kind
    // lookup a seek regardless of how the playlist's content mix skews.
    private static void ConfigurePlaylistItemForeignKeyIndexes(ModelBuilder modelBuilder)
    {
        (string Column, string Name)[] foreignKeyIndexes =
        [
            (nameof(PlaylistItem.MovieId), "IX_PlaylistItems_MovieId"),
            (nameof(PlaylistItem.TvId), "IX_PlaylistItems_TvId"),
            (nameof(PlaylistItem.EpisodeId), "IX_PlaylistItems_EpisodeId"),
            (nameof(PlaylistItem.SpecialId), "IX_PlaylistItems_SpecialId"),
        ];

        foreach ((string column, string name) in foreignKeyIndexes)
            modelBuilder
                .Entity<PlaylistItem>()
                .HasIndex(column)
                .HasDatabaseName(name)
                .HasFilter($"{column} IS NOT NULL");
    }

    private static void ConfigureImageForeignKeyIndexes(ModelBuilder modelBuilder)
    {
        // Each image belongs to exactly one owner, so every owner-FK column is NULL on
        // the vast majority of the 400k+ rows. A plain index over an all-or-mostly-NULL
        // column is statistically non-selective: SQLite's planner sees a single huge
        // bucket and falls back to a full table scan (loading one artist's album images
        // took ~2.6s for zero matches). Filtering each index to its non-NULL rows keeps
        // it small and selective, so these lookups stay index seeks no matter the mix of
        // media (a movie-only library has all-NULL music FKs, and vice versa).
        (string Column, string Name)[] foreignKeyIndexes =
        [
            (nameof(Image.TvId), "IX_Images_TvId"),
            (nameof(Image.SeasonId), "IX_Images_SeasonId"),
            (nameof(Image.EpisodeId), "IX_Images_EpisodeId"),
            (nameof(Image.MovieId), "IX_Images_MovieId"),
            (nameof(Image.CollectionId), "IX_Images_CollectionId"),
            (nameof(Image.PersonId), "IX_Images_PersonId"),
            (nameof(Image.CastCreditId), "IX_Images_CastCreditId"),
            (nameof(Image.CrewCreditId), "IX_Images_CrewCreditId"),
            (nameof(Image.ArtistId), "IX_Images_ArtistId"),
            (nameof(Image.AlbumId), "IX_Images_AlbumId"),
            (nameof(Image.TrackId), "IX_Images_TrackId"),
            (nameof(Image.CastId), "IX_Images_CastId"),
            (nameof(Image.CrewId), "IX_Images_CrewId"),
        ];

        foreach ((string column, string name) in foreignKeyIndexes)
            modelBuilder
                .Entity<Image>()
                .HasIndex(column)
                .HasDatabaseName(name)
                .HasFilter($"{column} IS NOT NULL");
    }

    // Cast/Crew rows each belong to exactly one of Movie/Tv/Season/Episode, so those
    // owner-FK columns are NULL on the vast majority of rows (same pathology as
    // ConfigureImageForeignKeyIndexes above — a plain index over a mostly-NULL column
    // is non-selective and SQLite's planner falls back to a full table scan, e.g. every
    // TV show page load full-scanning 112k Casts rows for zero SeasonId/EpisodeId
    // matches). Filtering each index to its non-NULL rows keeps it small and seekable
    // regardless of library mix. Roles.GuestStarId is a genuine one-to-one FK (a Role
    // either belongs to a Cast credit or a GuestStar, never both) so its uniqueness is
    // preserved on the filtered index.
    private static void ConfigureCreditForeignKeyIndexes(ModelBuilder modelBuilder)
    {
        (string Column, string Name)[] castIndexes =
        [
            (nameof(Cast.MovieId), "IX_Casts_MovieId"),
            (nameof(Cast.TvId), "IX_Casts_TvId"),
            (nameof(Cast.SeasonId), "IX_Casts_SeasonId"),
            (nameof(Cast.EpisodeId), "IX_Casts_EpisodeId"),
        ];

        foreach ((string column, string name) in castIndexes)
            modelBuilder
                .Entity<Cast>()
                .HasIndex(column)
                .HasDatabaseName(name)
                .HasFilter($"{column} IS NOT NULL");

        (string Column, string Name)[] crewIndexes =
        [
            (nameof(Crew.MovieId), "IX_Crews_MovieId"),
            (nameof(Crew.TvId), "IX_Crews_TvId"),
            (nameof(Crew.SeasonId), "IX_Crews_SeasonId"),
            (nameof(Crew.EpisodeId), "IX_Crews_EpisodeId"),
        ];

        foreach ((string column, string name) in crewIndexes)
            modelBuilder
                .Entity<Crew>()
                .HasIndex(column)
                .HasDatabaseName(name)
                .HasFilter($"{column} IS NOT NULL");

        modelBuilder
            .Entity<Role>()
            .HasIndex(nameof(Role.GuestStarId))
            .HasDatabaseName("IX_Roles_GuestStarId")
            .IsUnique()
            .HasFilter("GuestStarId IS NOT NULL");
    }

    private static void ConfigureColorPaletteIndexes(ModelBuilder modelBuilder)
    {
        // Filtered indexes for pending color-palette rows (NULL or empty value).
        // Allows the backfill cursor query to use an index scan instead of a full table scan.
        modelBuilder
            .Entity<Movie>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Movies_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Tv>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_TvShows_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Season>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Seasons_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Episode>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Episodes_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Collection>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Collections_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Person>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_People_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Recommendation>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Recommendations_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Similar>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Similar_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Image>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Images_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Artist>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Artists_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Album>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Albums_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Track>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Tracks_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<Playlist>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_Playlists_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");

        modelBuilder
            .Entity<ReleaseGroup>()
            .HasIndex(nameof(ColorPalettes._colorPalette))
            .HasDatabaseName("IX_ReleaseGroups_ColorPalette_pending")
            .HasFilter("ColorPalette IS NULL OR ColorPalette = ''");
    }
}
