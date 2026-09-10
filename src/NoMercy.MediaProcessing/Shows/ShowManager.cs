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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Common;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Networks;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Providers.TMDB.Models.TV;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Shows;

public class ShowManager(
    IShowRepository showRepository,
    JobDispatcher jobDispatcher,
    IStorageFactory storageFactory,
    IMediaTypeClassifier mediaTypeClassifier,
    IAnimeEnrichmentService animeEnrichmentService,
    ILogger<ShowManager> logger
) : BaseManager, IShowManager
{
    public async Task<TmdbTvShowAppends?> AddShowAsync(
        int id,
        Library library,
        bool? priority = false,
        string? addedBy = null
    )
    {
        logger.LogInformation("Show {Id}: Adding to Library {Title}", [id, library.Title]);

        using TmdbTvClient showClient = new(id);
        TmdbTvShowAppends? showAppends = await MetadataRetry.FetchAsync(
            () => showClient.WithAllAppends(priority),
            $"TMDB tv {id}"
        );

        if (showAppends == null)
            return null;

        string baseUrl = BaseUrl(showAppends.Name, showAppends.FirstAirDate);
        string? mediaType = await mediaTypeClassifier.ClassifyAsync(showAppends);

        (Library resolvedLibrary, DateTime folderCreatedAt, bool folderDateIsReal) =
            await ResolveLibraryAndCreatedAtAsync(id, library, baseUrl, mediaType);
        library = resolvedLibrary;

        Tv show = new()
        {
            LibraryId = library.Id,
            Folder = baseUrl,
            MediaType = mediaType,

            Id = showAppends.Id,
            Backdrop = showAppends.BackdropPath,
            FirstAirDate = showAppends.FirstAirDate,
            HaveEpisodes = 0,
            Homepage = showAppends.Homepage?.ToString(),
            ImdbId = showAppends.ExternalIds.ImdbId,
            InProduction = showAppends.InProduction,
            LastEpisodeToAir = showAppends.LastEpisodeToAir?.Id,
            NextEpisodeToAir = showAppends.NextEpisodeToAir?.Id,
            NumberOfEpisodes = showAppends.NumberOfEpisodes,
            NumberOfSeasons = showAppends.NumberOfSeasons,
            OriginalLanguage = showAppends.OriginalLanguage,
            Overview = showAppends.Overview,
            Popularity = showAppends.Popularity,
            Poster = showAppends.PosterPath,
            Status = showAppends.Status,
            Tagline = showAppends.Tagline,
            Title = showAppends.Name,
            TitleSort = showAppends.Name?.TitleSort(showAppends.FirstAirDate) ?? string.Empty,
            TvdbId = showAppends.ExternalIds.TvdbId,
            Type = showAppends.Type,
            VoteAverage = showAppends.VoteAverage,
            VoteCount = showAppends.VoteCount,

            CreatedAt = folderCreatedAt,

            Duration =
                showAppends.EpisodeRunTime?.Length > 0
                    ? (int?)showAppends.EpisodeRunTime?.Average()
                    : 0,
            OriginCountry =
                showAppends.OriginCountry.Length > 0 ? showAppends.OriginCountry[0] : null,
            SpokenLanguages =
                showAppends.SpokenLanguages.Length > 0 ? showAppends.SpokenLanguages[0].Name : null,
            Trailer =
                showAppends.Videos.Results.Length > 0 ? showAppends.Videos.Results[0].Key : null,
        };

        await showRepository.AddAsync(show, folderDateIsReal);
        logger.LogDebug("Show {Title}: Added to Database", show.Title);

        await showRepository.LinkToLibrary(library, show, addedBy);
        logger.LogDebug("Show {Title}: Linked to Library {Title2}", [show.Title, library.Title]);

        await StoreGenres(showAppends);
        await animeEnrichmentService.EnrichTvAsync(
            show.Id,
            showAppends.Name ?? string.Empty,
            showAppends.FirstAirDate.ParseYear(),
            showAppends.OriginCountry
        );
        await StoreContentRatings(showAppends);
        await StoreTranslations(showAppends);

        logger.LogInformation(
            "Show {Name}: Added to Library {Title}",
            [showAppends.Name, library.Title]
        );

        jobDispatcher.DispatchColorPaletteJob("tv", show.Id.ToString());
        jobDispatcher.DispatchJob<ShowExtrasJob, TmdbTvShowAppends>(showAppends);

        return showAppends;
    }

    // The folder is ground truth, not the classifier: if this show's folder
    // physically exists under the library it was scanned from, that is
    // structural proof of where it belongs, and no text-match verdict gets a
    // vote. The classifier is only consulted when the scanned library has no
    // on-disk trace of the folder at all (e.g. a manually-added show with no
    // file yet) - and even then it may only PROMOTE toward anime, never
    // evict, and only into a library whose own folders can be shown to
    // contain the same folder. Split out from AddShowAsync (which needs a
    // live TMDB client) so this decision is unit-testable with mocked
    // storage/repository alone.
    internal async Task<(
        Library library,
        DateTime createdAt,
        bool folderDateIsReal
    )> ResolveLibraryAndCreatedAtAsync(
        int id,
        Library scannedLibrary,
        string baseUrl,
        string? mediaType
    )
    {
        (bool existsInScannedLibrary, DateTime createdAt) = ResolveFolder(scannedLibrary, baseUrl);

        if (existsInScannedLibrary)
            return (scannedLibrary, createdAt, true);

        if (!ShouldPromoteToAnimeLibrary(scannedLibrary.Type, mediaType))
            return (scannedLibrary, createdAt, false);

        Library? animeLibrary = await showRepository.GetLibraryByTypeAsync(
            MediaTypes.AnimeMediaType
        );

        if (animeLibrary is null)
        {
            logger.LogWarning(
                "Show {Id}: Classified as anime but no anime library exists; keeping original Library {Title}",
                [id, scannedLibrary.Title]
            );
            return (scannedLibrary, createdAt, false);
        }

        (bool existsInAnimeLibrary, DateTime animeCreatedAt) = ResolveFolder(animeLibrary, baseUrl);

        if (!existsInAnimeLibrary)
            return (scannedLibrary, createdAt, false);

        logger.LogInformation(
            "Show {Id}: Reclassified as {MediaType}, filing under Library {Title} instead of {OriginalTitle}",
            [id, mediaType, animeLibrary.Title, scannedLibrary.Title]
        );
        return (animeLibrary, animeCreatedAt, true);
    }

    // Pure decision, split out so the "never evict from the scanned library"
    // invariant is unit-testable without standing up TMDB/storage mocks for
    // the rest of AddShowAsync.
    internal static bool ShouldPromoteToAnimeLibrary(
        string scannedLibraryType,
        string? mediaType
    ) => mediaType == MediaTypes.AnimeMediaType && scannedLibraryType != MediaTypes.AnimeMediaType;

    // Structural ground truth: does this show's folder physically exist under
    // ANY of the given library's configured folders? Returns the real folder
    // creation date when found, or DateTime.UtcNow (the "not found" sentinel
    // the caller must not mistake for a real date) when it does not.
    private (bool exists, DateTime createdAt) ResolveFolder(Library library, string baseUrl)
    {
        DateTime folderCreatedAt = DateTime.UtcNow;

        foreach (FolderLibrary folderLibrary in library.FolderLibraries ?? [])
        {
            if (storageFactory == null)
                continue;

            IStorage folderStorage = storageFactory.For(
                folderLibrary.Folder.Id,
                folderLibrary.Folder.DriverId,
                string.Empty
            );
            string folderRoot = FolderRootPath(folderStorage, folderLibrary.Folder.Path);
            string folderName = folderStorage.CombinePath(folderRoot, baseUrl.Replace("/", ""));

            if (!folderStorage.Exists(folderName))
            {
                string? match = FileNameSanitizer.FindMatchingDirectory(
                    folderStorage.Driver,
                    folderRoot,
                    baseUrl.Replace("/", "")
                );
                if (match != null)
                    folderName = match;
            }

            if (!folderStorage.Exists(folderName))
                continue;

            folderCreatedAt = folderStorage.Driver.GetCreationTimeUtc(folderName);
            return (true, folderCreatedAt);
        }

        return (false, folderCreatedAt);
    }

    public Task UpdateShowAsync(int id, Library library)
    {
        // Re-importing refreshes all metadata; every write is an idempotent
        // upsert, so re-running AddShowAsync updates existing rows in place.
        return AddShowAsync(id, library);
    }

    public async Task RemoveShowAsync(int id)
    {
        await showRepository.Remove(id);
        logger.LogDebug("Show {Id}: Removed from Database", id);
    }

    internal async Task StoreAlternativeTitles(TmdbTvShowAppends show)
    {
        IEnumerable<AlternativeTitle> alternativeTitles = show.AlternativeTitles.Results.Select(
            tmdbShowAlternativeTitles => new AlternativeTitle
            {
                Iso31661 = tmdbShowAlternativeTitles.Iso31661,
                Title = tmdbShowAlternativeTitles.Title,
                TvId = show.Id,
            }
        );

        await showRepository.StoreAlternativeTitles(alternativeTitles);

        logger.LogDebug("Show {Name}: AlternativeTitles stored", show.Name);
    }

    internal async Task StoreTranslations(TmdbTvShowAppends show)
    {
        List<Translation> translations = show
            .Translations.Translations.Select(translation => new Translation
            {
                Iso31661 = translation.Iso31661,
                Iso6391 = translation.Iso6391,
                Name = translation.Name == "" ? null : translation.Name,
                Title = translation.Data.Title == "" ? null : translation.Data.Title,
                Overview = translation.Data.Overview == "" ? null : translation.Data.Overview,
                EnglishName = translation.EnglishName,
                Homepage = translation.Data.Homepage?.ToString(),
                Biography = translation.Data.Biography,
                TvId = show.Id,
            })
            .ToList();

        await showRepository.StoreTranslations(translations);

        logger.LogDebug("Show {Name}: Translations stored", show.Name);
    }

    internal async Task StoreContentRatings(TmdbTvShowAppends show)
    {
        List<CertificationCriteria> certificationCriteria = show
            .ContentRatings.Results.Select(r => new CertificationCriteria
            {
                Iso31661 = r.Iso31661,
                Certification = r.Rating,
            })
            .ToList();

        IEnumerable<CertificationTv> certificationTvs = showRepository.GetCertificationTvs(
            show,
            certificationCriteria
        );

        await showRepository.StoreContentRatings(certificationTvs);

        logger.LogDebug("Show {Name}: Content Ratings stored", show.Name);
    }

    internal async Task StoreSimilar(TmdbTvShowAppends show)
    {
        IEnumerable<Similar> similar = show
            .Similar.Results.Select(similar => new Similar
            {
                Backdrop = similar.BackdropPath,
                Overview = similar.Overview,
                Poster = similar.PosterPath,
                Title = similar.Name,
                TitleSort = similar.Name,
                MediaId = similar.Id,
                TvFromId = show.Id,
            })
            .ToArray();

        await showRepository.StoreSimilar(similar);

        logger.LogDebug("Show {Name}: Similar stored", show.Name);

        await using MediaContext db = new();
        List<int> similarIds = await db
            .Similar.AsNoTracking()
            .Where(s => s.TvFromId == show.Id && (s._colorPalette == null || s._colorPalette == ""))
            .Select(s => s.Id)
            .ToListAsync();

        foreach (int id in similarIds)
            jobDispatcher.DispatchColorPaletteJob("similar", id.ToString());
    }

    internal async Task StoreRecommendations(TmdbTvShowAppends show)
    {
        IEnumerable<Recommendation> recommendations = show
            .Recommendations.Results.Select(recommendation => new Recommendation
            {
                Backdrop = recommendation.BackdropPath,
                Overview = recommendation.Overview,
                Poster = recommendation.PosterPath,
                Title = recommendation.Name,
                TitleSort = recommendation.Name.TitleSort(),
                MediaId = recommendation.Id,
                TvFromId = show.Id,
            })
            .ToArray();

        await showRepository.StoreRecommendations(recommendations);

        logger.LogDebug("Show {Name}: Recommendations stored", show.Name);

        await using MediaContext db = new();
        List<int> recommendationIds = await db
            .Recommendations.AsNoTracking()
            .Where(r => r.TvFromId == show.Id && (r._colorPalette == null || r._colorPalette == ""))
            .Select(r => r.Id)
            .ToListAsync();

        foreach (int id in recommendationIds)
            jobDispatcher.DispatchColorPaletteJob("recommendation", id.ToString());
    }

    internal async Task StoreVideos(TmdbTvShowAppends show)
    {
        IEnumerable<Media> videos = show.Videos.Results.Select(media => new Media
        {
            Id = Ulid.NewUlid(),
            Iso6391 = media.Iso6391,
            Name = media.Name,
            Site = media.Site,
            Size = media.Size,
            Src = media.Key,
            Type = media.Type,
            TvId = show.Id,
        });

        await showRepository.StoreVideos(videos);

        logger.LogDebug("Show {Name}: Videos stored", show.Name);
    }

    internal async Task StoreImages(TmdbTvShowAppends show)
    {
        IEnumerable<Image> posters = show
            .Images.Posters.Select(image => new Image
            {
                AspectRatio = image.AspectRatio,
                Height = image.Height,
                Iso6391 = image.Iso6391,
                FilePath = image.FilePath,
                Width = image.Width,
                VoteAverage = image.VoteAverage,
                VoteCount = image.VoteCount,
                TvId = show.Id,
                Type = "poster",
                Site = "https://image.tmdb.org/t/p/",
            })
            .ToArray();

        await showRepository.StoreImages(posters);

        IEnumerable<Image> backdrops = show
            .Images.Backdrops.Select(image => new Image
            {
                AspectRatio = image.AspectRatio,
                Height = image.Height,
                Iso6391 = image.Iso6391,
                FilePath = image.FilePath,
                Width = image.Width,
                VoteAverage = image.VoteAverage,
                VoteCount = image.VoteCount,
                TvId = show.Id,
                Type = "backdrop",
                Site = "https://image.tmdb.org/t/p/",
            })
            .ToArray();

        await showRepository.StoreImages(backdrops);
        logger.LogDebug("Show {Name}: backdrops stored", show.Name);

        IEnumerable<Image> logos = show
            .Images.Logos.Select(image => new Image
            {
                AspectRatio = image.AspectRatio,
                Height = image.Height,
                Iso6391 = image.Iso6391,
                FilePath = image.FilePath,
                Width = image.Width,
                VoteAverage = image.VoteAverage,
                VoteCount = image.VoteCount,
                TvId = show.Id,
                Type = "logo",
                Site = "https://image.tmdb.org/t/p/",
            })
            .ToArray();

        await showRepository.StoreImages(logos);
        logger.LogDebug("Show {Name}: Logos stored", show.Name);

        await using MediaContext db = new();
        List<int> imageIds = await db
            .Images.AsNoTracking()
            .Where(i => i.TvId == show.Id && (i._colorPalette == null || i._colorPalette == ""))
            .Select(i => i.Id)
            .ToListAsync();

        foreach (int id in imageIds)
            jobDispatcher.DispatchColorPaletteJob("image", id.ToString());
    }

    internal async Task StoreKeywords(TmdbTvShowAppends show)
    {
        IEnumerable<Keyword> keywords = show.Keywords.Results.Select(keyword => new Keyword
        {
            Id = keyword.Id,
            Name = keyword.Name,
        });

        await showRepository.StoreKeywords(keywords);
        logger.LogDebug("Show {Name}: Keywords stored", show.Name);

        IEnumerable<KeywordTv> keywordTvs = show.Keywords.Results.Select(keyword => new KeywordTv
        {
            KeywordId = keyword.Id,
            TvId = show.Id,
        });

        await showRepository.LinkKeywordsToTv(keywordTvs);
        logger.LogDebug("Show {Name}: Keywords linked to Show", show.Name);
    }

    internal async Task StoreGenres(TmdbTvShowAppends show)
    {
        IEnumerable<GenreTv> genreShows = show.Genres.Select(genre => new GenreTv
        {
            GenreId = genre.Id,
            TvId = show.Id,
        });

        await showRepository.StoreGenres(genreShows);
        logger.LogDebug("Show {Name}: Genres stored", show.Name);
    }

    internal async Task StoreWatchProviders(TmdbTvShowAppends show)
    {
        List<WatchProvider> watchProviders = [];
        List<WatchProviderMedia> watchProviderMedias = [];

        foreach (
            (
                string countryCode,
                string providerType,
                TmdbPaymentDetails provider,
                string? link
            ) in TmdbWatchProviders.ExtractProviders(show.WatchProviders.TmdbWatchProviderResults)
        )
        {
            if (watchProviders.All(wp => wp.Id != provider.ProviderId))
            {
                watchProviders.Add(
                    new()
                    {
                        Id = provider.ProviderId,
                        Name = provider.ProviderName,
                        Logo = provider.LogoPath,
                        DisplayPriority = provider.DisplayPriority,
                    }
                );
            }

            watchProviderMedias.Add(
                new()
                {
                    WatchProviderId = provider.ProviderId,
                    TvId = show.Id,
                    CountryCode = countryCode,
                    ProviderType = providerType,
                    Link = link,
                }
            );
        }

        if (watchProviders.Count != 0)
            await showRepository.StoreWatchProviders(watchProviders);

        if (watchProviderMedias.Count != 0)
            await showRepository.StoreWatchProviderMedias(watchProviderMedias);

        logger.LogDebug("Show {Name}: WatchProviders stored", show.Name);
    }

    internal async Task StoreNetworks(TmdbTvShowAppends show)
    {
        if (show.Networks.Length == 0)
        {
            logger.LogDebug("Show {Name}: No networks found", show.Name);
            return;
        }

        TmdbTvClient showClient = new(show.Id);

        List<Network> networks = [];

        foreach (TmdbNetwork network in show.Networks)
        {
            TmdbTmdbNetworkDetails? nw = await showClient.NetworkDetails(network.Id);
            if (nw == null)
                continue;

            if (networks.All(n => n.Id != nw.Id))
            {
                networks.Add(
                    new()
                    {
                        Id = nw.Id,
                        Name = nw.Name,
                        Logo = nw.LogoPath,
                        OriginCountry = nw.OriginCountry,
                        Headquarters = nw.Headquarters,
                        Homepage = nw.Homepage,
                    }
                );
            }
        }

        List<NetworkTv> networkTvs = show
            .Networks.Select(network => new NetworkTv { NetworkId = network.Id, TvId = show.Id })
            .ToList();

        if (networks.Count != 0)
            await showRepository.StoreNetworks(networks);

        if (networkTvs.Count != 0)
            await showRepository.StoreNetworkTvs(networkTvs);

        logger.LogDebug("Show {Name}: Networks stored", show.Name);
    }

    internal async Task StoreCompanies(TmdbTvShowAppends show)
    {
        if (show.ProductionCompanies.Length == 0)
        {
            logger.LogDebug("Show {Name}: No production companies found", show.Name);
            return;
        }

        TmdbTvClient showClient = new(show.Id);

        ConcurrentDictionary<int, Company> companiesDict = new();

        await Parallel.ForEachAsync(
            show.ProductionCompanies,
            SystemParallelism.Options,
            async (productionCompany, _) =>
            {
                TmdbTmdbNetworkDetails? nw = await showClient.CompanyDetails(productionCompany.Id);
                if (nw == null)
                    return;

                companiesDict.TryAdd(
                    nw.Id,
                    new()
                    {
                        Id = nw.Id,
                        Name = nw.Name,
                        Logo = nw.LogoPath,
                        OriginCountry = nw.OriginCountry,
                        Headquarters = nw.Headquarters,
                        Homepage = nw.Homepage,
                    }
                );
            }
        );

        List<Company> companies = companiesDict.Values.ToList();

        List<CompanyTv> companyTvs = companies
            .Select(company => new CompanyTv { CompanyId = company.Id, TvId = show.Id })
            .ToList();

        if (companies.Count != 0)
            await showRepository.StoreCompanies(companies);

        if (companyTvs.Count != 0)
            await showRepository.StoreCompanyTvs(companyTvs);

        logger.LogDebug("Show {Name}: Companies stored", show.Name);
    }
}
