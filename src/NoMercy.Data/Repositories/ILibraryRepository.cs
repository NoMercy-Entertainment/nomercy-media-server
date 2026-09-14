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

using System.Linq.Expressions;
using NoMercy.Data.DTOs;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Data.Repositories;

public interface ILibraryRepository
{
    Task<List<Library>> GetLibraries(Guid userId, CancellationToken ct = default);

    Task<List<Library>> GetLibrariesLite(Guid userId, CancellationToken ct = default);

    Task<Dictionary<Ulid, int>> GetLibraryItemCountsAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<Library?> GetLibraryByIdAsync(
        Ulid libraryId,
        Guid userId,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    );

    IAsyncEnumerable<Movie> GetLibraryMovies(
        MediaContext mediaContext,
        Guid userId,
        Ulid libraryId,
        string language,
        int take,
        int skip,
        Expression<Func<Movie, object>>? orderByExpression,
        string? direction
    );

    IAsyncEnumerable<Tv> GetLibraryShows(
        MediaContext mediaContext,
        Guid userId,
        Ulid libraryId,
        string language,
        int take,
        int skip,
        Expression<Func<Tv, object>>? orderByExpression,
        string? direction
    );

    Task<List<MovieCardDto>> GetLibraryMovieCardsAsync(
        Guid userId,
        Ulid libraryId,
        string country,
        int take,
        int skip,
        CancellationToken ct = default
    );

    Task<List<MovieCardDto>> GetLibraryMovieCardsAsync(
        MediaContext mediaContext,
        Guid userId,
        Ulid libraryId,
        string country,
        int take,
        int skip,
        CancellationToken ct = default
    );

    Task<List<TvCardDto>> GetLibraryTvCardsAsync(
        Guid userId,
        Ulid libraryId,
        string country,
        int take,
        int skip,
        CancellationToken ct = default
    );

    Task<List<TvCardDto>> GetLibraryTvCardsAsync(
        MediaContext mediaContext,
        Guid userId,
        Ulid libraryId,
        string country,
        int take,
        int skip,
        CancellationToken ct = default
    );

    Task<List<Movie>> GetPaginatedLibraryMovies(
        Guid userId,
        Ulid libraryId,
        string letter,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<List<Tv>> GetPaginatedLibraryShows(
        Guid userId,
        Ulid libraryId,
        string letter,
        string language,
        string country,
        int take,
        int page,
        Expression<Func<Tv, object>>? orderByExpression = null,
        string? direction = null,
        CancellationToken ct = default
    );

    Task<List<HomeMovieCardDto>> GetPaginatedLibraryMovieCardsAsync(
        Guid userId,
        Ulid libraryId,
        string letter,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<List<HomeTvCardDto>> GetPaginatedLibraryTvCardsAsync(
        Guid userId,
        Ulid libraryId,
        string letter,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<Library?> GetLibraryByIdAsync(Ulid id);

    Task<Library?> GetLibraryByIdLiteAsync(Ulid id, CancellationToken ct = default);

    Task<bool> HasCompletedSetupAsync(CancellationToken ct = default);

    Task<Library?> GetLibraryByTypeAsync(
        string type,
        string? fallbackType = null,
        CancellationToken ct = default
    );

    Task<VideoSearchResults> SearchVideoByTitleAsync(
        string normalizedQuery,
        CancellationToken ct = default
    );

    Task<List<Library>> GetAllLibrariesAsync();

    Task<List<FolderDto>> GetFoldersAsync();

    Task<Tv?> GetRandomTvShow(Guid userId, string language, CancellationToken ct = default);

    Task<HomeTvCardDto?> GetRandomTvCardAsync(
        Guid userId,
        string language,
        string country,
        CancellationToken ct = default
    );

    Task<Movie?> GetRandomMovie(Guid userId, string language, CancellationToken ct = default);

    Task<HomeMovieCardDto?> GetRandomMovieCardAsync(
        Guid userId,
        string language,
        string country,
        CancellationToken ct = default
    );

    Task AddLibraryAsync(Library library, Guid userId);

    Task UpdateLibraryAsync(Library library);

    Task SetLibraryLanguagesAsync(Ulid libraryId, IEnumerable<int> languageIds);

    Task DeleteLibraryAsync(Library library);

    Task<int> AddEncodingPresetFolderAsync(EncodingPresetFolder encodingPresetFolder);

    Task<int> AddEncodingPresetFolderAsync(List<EncodingPresetFolder> encodingPresetFolders);

    Task<int> AddEncodingPresetFolderAsync(EncodingPresetFolder[] encodingPresetFolders);

    Task<int> AddLanguageLibraryAsync(LanguageLibrary[] languageLibraries);

    Task<int> SyncEncodingPresetFolderAsync(
        List<EncodingPresetFolder> encodingPresetFolders,
        List<Folder> folders
    );

    /// <summary>A library's import failures, newest attempt first, optionally by resolved state.</summary>
    Task<List<ImportFailure>> GetImportFailuresAsync(
        Ulid libraryId,
        bool? resolved,
        CancellationToken ct = default
    );
}

public record VideoSearchResults(List<Tv> Tvs, List<Movie> Movies);
