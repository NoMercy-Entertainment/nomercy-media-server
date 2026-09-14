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
using NoMercy.Api.DTOs.Media;
using NoMercy.Database.Models.Libraries;

namespace NoMercy.Api.Services;

/// <summary>
/// The library section's entries, in the order they are drawn: the viewer's
/// video libraries, the pages those libraries make meaningful, then music.
/// </summary>
public static class LibraryNavigation
{
    private static readonly LibraryNavigationEntryDto[] MoviePages =
    [
        Page("collections", "library.base.collections", "collection1", "/collection", "library"),
    ];

    private static readonly LibraryNavigationEntryDto[] VideoPages =
    [
        Page("specials", "library.base.specials", "sparkles", "/specials", "library"),
        Page("genres", "library.base.genres", "witchHat", "/genres", "library"),
        Page("people", "library.base.people", "user", "/person", "library"),
        Page("favorites", "library.base.favorites", "heart", "/favorites", "library"),
        Page("lists", "library.base.my_lists", "bulletList", "/lists", "library"),
    ];

    private static readonly LibraryNavigationEntryDto[] AnimePages =
    [
        Page("anime-themes", "library.base.anime_themes", "witchHat", "/anime/themes", "library"),
        Page(
            "anime-demographics",
            "library.base.anime_demographics",
            "user",
            "/anime/demographics",
            "library"
        ),
        Page(
            "anime-seasons",
            "library.base.anime_seasons",
            "collection1",
            "/anime/seasons",
            "library"
        ),
    ];

    private static readonly LibraryNavigationEntryDto[] MusicPages =
    [
        Page("MusicStart", "Start", "folder", "/music/start", "music"),
        Page("MusicArtists", "Artists", "speaker", "/music/artists", "music"),
        Page("MusicAlbums", "Albums", "disk", "/music/albums", "music"),
        Page("MusicGenres", "Genres", "noteClefTreble", "/music/genres", "music"),
        Page("MusicFavorites", "Songs you like", "heart", "/music/favorites", "music"),
    ];

    public static bool HasVideo(IEnumerable<Library> libraries) =>
        libraries.Any(library => library.Type != "music");

    /// <param name="hasAnime">Anime pages are offered only once anime exists, so they never open on an empty grid.</param>
    public static List<LibraryNavigationEntryDto> Build(
        IReadOnlyCollection<Library> libraries,
        bool hasAnime,
        IEnumerable<LibraryNavigationEntryDto> videoPluginEntries,
        IEnumerable<LibraryNavigationEntryDto> musicPluginEntries
    )
    {
        List<LibraryNavigationEntryDto> entries =
        [
            .. libraries
                .Where(library => library.Type != "music")
                .OrderBy(library => library.Order)
                .Select(ForLibrary),
        ];

        if (libraries.Any(library => library.Type == "movie"))
            entries.AddRange(MoviePages);

        if (HasVideo(libraries))
        {
            entries.AddRange(VideoPages);
            if (hasAnime)
                entries.AddRange(AnimePages);
        }

        entries.AddRange(videoPluginEntries);
        entries.AddRange(MusicPages);
        entries.AddRange(musicPluginEntries);
        return entries;
    }

    private static LibraryNavigationEntryDto ForLibrary(Library library) =>
        new()
        {
            Id = library.Id.ToString(),
            Label = library.Title,
            Icon = IconForLibraryType(library.Type),
            Link = $"/libraries/{library.Id}",
            Origin = LibraryNavigationOrigin.Library,
            RouteType = "library",
        };

    /// <summary>
    /// A library the app has no glyph for is still a library: it gets the folder
    /// rather than nothing, which is what an unmapped type used to draw.
    /// </summary>
    private static string IconForLibraryType(string? type) =>
        type switch
        {
            "anime" or "tv" => "monitor",
            "movie" => "movieClap",
            "music" => "noteDouble",
            _ => "folder",
        };

    private static LibraryNavigationEntryDto Page(
        string id,
        string label,
        string icon,
        string link,
        string routeType
    ) =>
        new()
        {
            Id = id,
            Label = label,
            Icon = icon,
            Link = link,
            Origin = LibraryNavigationOrigin.Page,
            RouteType = routeType,
        };
}
