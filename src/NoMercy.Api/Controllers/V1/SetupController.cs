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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Api.Controllers.V1;

[ApiController]
[Tags("App Setup")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/setup")]
public class SetupController(
    MediaContext context,
    IServerConfigurationRepository serverConfiguration,
    SetupService setupService,
    HomeService homeService,
    ILibraryRepository libraryRepository,
    IPluginManager pluginManager
) : BaseController
{
    /// <summary>
    /// Every way into the library section, in the order they are drawn.
    ///
    /// <para>
    /// Which of these exist is not a client's question to answer. A viewer
    /// granted only a music library has no people, specials or genres to browse,
    /// and a plugin's page exists only while that plugin is enabled — both are
    /// facts the server holds. The clients each carried their own copy of this
    /// list and drifted, and a plugin that mounted into the library section had
    /// nowhere to appear at all.
    /// </para>
    ///
    /// <para>
    /// Lives under Setup, not Libraries: the app calls this as a setup step,
    /// before it necessarily has a library of its own to route into.
    /// </para>
    /// </summary>
    [HttpGet]
    [Route("navigation")]
    public async Task<IActionResult> Navigation(CancellationToken ct = default)
    {
        Guid userId = User.UserId();

        List<Library> libraries = await libraryRepository.GetLibraries(userId, ct);
        List<LibraryNavigationEntryDto> entries = [];

        foreach (
            Library library in libraries
                .Where(library => library.Type != "music")
                .OrderBy(library => library.Order)
        )
        {
            entries.Add(
                new()
                {
                    Id = library.Id.ToString(),
                    Label = library.Title,
                    Icon = IconForLibraryType(library.Type),
                    Link = $"/libraries/{library.Id}",
                    Origin = LibraryNavigationOrigin.Library,
                    RouteType = "library",
                }
            );
        }

        bool hasVideo = libraries.Any(library => library.Type != "music");
        bool hasMovies = libraries.Any(library => library.Type == "movie");

        if (hasMovies)
        {
            entries.Add(
                Page(
                    id: "collections",
                    label: "library.base.collections",
                    icon: "collection1",
                    link: "/collection",
                    routeType: "library"
                )
            );
        }

        if (hasVideo)
        {
            entries.Add(
                Page(
                    id: "specials",
                    label: "library.base.specials",
                    icon: "sparkles",
                    link: "/specials",
                    routeType: "library"
                )
            );
            entries.Add(
                Page(
                    id: "genres",
                    label: "library.base.genres",
                    icon: "witchHat",
                    link: "/genres",
                    routeType: "library"
                )
            );
            entries.Add(
                Page(
                    id: "people",
                    label: "library.base.people",
                    icon: "user",
                    link: "/person",
                    routeType: "library"
                )
            );
            entries.Add(
                Page(
                    id: "favorites",
                    label: "library.base.favorites",
                    icon: "heart",
                    link: "/favorites",
                    routeType: "library"
                )
            );
            entries.Add(
                Page(
                    id: "lists",
                    label: "library.base.my_lists",
                    icon: "bulletList",
                    link: "/lists",
                    routeType: "library"
                )
            );

            // Only surfaced once the user actually has anime — these tables stay
            // empty for a library with no anime, and there is no point offering
            // a browse entry that always renders an empty grid.
            bool hasAnime =
                await context.AnimeThemeTv.AsNoTracking().AnyAsync(ct)
                || await context.AnimeThemeMovie.AsNoTracking().AnyAsync(ct);

            if (hasAnime)
            {
                entries.Add(
                    Page(
                        id: "anime-themes",
                        label: "library.base.anime_themes",
                        icon: "witchHat",
                        link: "/anime/themes",
                        routeType: "library"
                    )
                );
                entries.Add(
                    Page(
                        id: "anime-demographics",
                        label: "library.base.anime_demographics",
                        icon: "user",
                        link: "/anime/demographics",
                        routeType: "library"
                    )
                );
                entries.Add(
                    Page(
                        id: "anime-seasons",
                        label: "library.base.anime_seasons",
                        icon: "collection1",
                        link: "/anime/seasons",
                        routeType: "library"
                    )
                );
            }
        }

        entries.AddRange(PluginEntries(PluginKind.Library, PluginKind.Video));

        entries.Add(
            new()
            {
                Id = "MusicStart",
                Label = "Start",
                Icon = IconForLibraryType("speaker"),
                Link = "/music/start",
                Origin = LibraryNavigationOrigin.Page,
                RouteType = "music",
            }
        );

        entries.Add(
            new()
            {
                Id = "MusicArtists",
                Label = "Artists",
                Icon = "speaker",
                Link = "/music/artists",
                Origin = LibraryNavigationOrigin.Page,
                RouteType = "music",
            }
        );

        entries.Add(
            new()
            {
                Id = "MusicAlbums",
                Label = "Albums",
                Icon = "disk",
                Link = "/music/albums",
                Origin = LibraryNavigationOrigin.Page,
                RouteType = "music",
            }
        );

        entries.Add(
            new()
            {
                Id = "MusicGenres",
                Label = "Genres",
                Icon = "noteClefTreble",
                Link = "/music/genres",
                Origin = LibraryNavigationOrigin.Page,
                RouteType = "music",
            }
        );

        entries.Add(
            new()
            {
                Id = "MusicFavorites",
                Label = "Songs you like",
                Icon = "heart",
                Link = "/music/favorites",
                Origin = LibraryNavigationOrigin.Page,
                RouteType = "music",
            }
        );

        entries.AddRange(PluginEntries(PluginKind.Music));

        return Ok(new DataResponseDto<List<LibraryNavigationEntryDto>> { Data = entries });
    }

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

    /// <summary>
    /// The pages plugins mount into this section. A plugin awaiting consent or
    /// disabled has no instance, so it contributes nothing — the entry appears
    /// the moment it is enabled and disappears again when it is not.
    /// </summary>
    private List<LibraryNavigationEntryDto> PluginEntries(params string[] kinds) =>
        [
            .. pluginManager
                .GetInstalledPlugins()
                .SelectMany(info =>
                    (pluginManager.GetPluginInstance(info.Id) as IUiPlugin)
                        ?.NavEntries.Where(entry => kinds.Contains(entry.Section))
                        .Select(entry => new LibraryNavigationEntryDto
                        {
                            Id = $"plugin-{info.Id}-{entry.Route.Trim('/')}".TrimEnd('-'),
                            Label = entry.Label,
                            Icon = entry.Icon ?? string.Empty,
                            Link =
                                PluginRoutes.PrefixFor(entry.Section, info.Id).TrimEnd('/')
                                + (entry.Route == "/" ? string.Empty : entry.Route),
                            Origin = LibraryNavigationOrigin.Plugin,
                            PluginId = info.Id,
                            RouteType = kinds.First(),
                        })
                    ?? []
                )
                .OrderBy(entry => entry.Label),
        ];

    [HttpGet("libraries")]
    public async Task<IActionResult> Libraries()
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view libraries");

        List<LibrariesResponseItemDto> response = (await setupService.GetSetupLibraries(userId))
            .Select(library => new LibrariesResponseItemDto(library))
            .ToList();

        return Ok(new LibrariesDto { Data = response.OrderBy(library => library.Order) });
    }

    [HttpGet]
    [Route("server-info")]
    [ResponseCache(NoStore = true, Duration = 0)]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> ServerInfo()
    {
        bool setupComplete = await libraryRepository.HasCompletedSetupAsync();
        string serverName = await serverConfiguration.GetServerNameAsync();

        return Ok(
            new StatusResponseDto<ServerInfoDto>
            {
                Status = "ok",
                Data = new()
                {
                    Server = serverName,
                    Cpu = Info.CpuNames,
                    Gpu = Info.GpuNames,
                    Os = $"{Info.Platform.ToTitleCase()} {Info.OsVersion}",
                    Arch = Info.Architecture,
                    Version = Software.GetReleaseVersion(),
                    BootTime = Info.StartTime,
                    SetupComplete = setupComplete,
                },
            }
        );
    }

    [HttpGet]
    [Route("permissions")]
    [Authorize(Policy = "MediaAccess")]
    public IActionResult Permissions()
    {
        return Ok(
            new
            {
                owner = AuthPolicy.IsOwner(User),
                manager = AuthPolicy.IsModerator(User),
                allowed = AuthPolicy.IsAllowed(User),
                optical_access = AuthPolicy.IsOpticalAccess(User),
            }
        );
    }

    [HttpGet("music-playlists")]
    public async Task<IActionResult> Index()
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view playlists");

        List<Playlist> playlistItems = await setupService.GetSetupPlaylistsAsync(userId);

        return Ok(
            new StatusResponseDto<List<PlaylistDto>>
            {
                Status = "ok",
                Data = playlistItems.Select(p => new PlaylistDto(p)).ToList(),
            }
        );
    }

    [HttpGet]
    [Route("screensaver")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Screensaver()
    {
        ScreensaverDto result = await homeService.GetSetupScreensaverContent(User.UserId());

        return Ok(result);
    }

    [HttpGet]
    [AllowAnonymous]
    [Route("/status")]
    [ResponseCache(Duration = 30)]
    public IActionResult Status()
    {
        return Ok(
            new
            {
                Status = "ok",
                Version = "1.0",
                Message = "NoMercy MediaServer API is running",
                Timestamp = DateTime.UtcNow,
            }
        );
    }
}
