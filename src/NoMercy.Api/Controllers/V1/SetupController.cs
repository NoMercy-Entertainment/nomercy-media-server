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
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
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
    IAnimeThemeRepository animeThemeRepository,
    IServerConfigurationRepository serverConfiguration,
    IMusicRepository musicRepository,
    HomeService homeService,
    ILibraryRepository libraryRepository,
    IPluginManager pluginManager,
    ILogger<SetupController> logger
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
        List<Library> libraries = await libraryRepository.GetLibraries(User.UserId(), ct);
        bool hasAnime =
            LibraryNavigation.HasVideo(libraries)
            && await animeThemeRepository.AnyThemedTitlesAsync(ct);

        List<LibraryNavigationEntryDto> entries = LibraryNavigation.Build(
            libraries,
            hasAnime,
            PluginEntries(PluginKind.Library, PluginKind.Video),
            PluginEntries(PluginKind.Music)
        );

        return Ok(new DataResponseDto<List<LibraryNavigationEntryDto>> { Data = entries });
    }

    /// <summary>
    /// The pages plugins mount into this section. A plugin awaiting consent or
    /// disabled has no instance, so it contributes nothing — the entry appears
    /// the moment it is enabled and disappears again when it is not.
    /// </summary>
    private List<LibraryNavigationEntryDto> PluginEntries(params string[] kinds) =>
        [
            .. pluginManager
                .GetInstalledPlugins()
                .SelectMany(info => EntriesOf(info, kinds) ?? [])
                .OrderBy(entry => entry.Label),
        ];

    /// <summary>
    /// One plugin's navigation entries, or none when that plugin was built
    /// against a contract member v3 took away.
    /// <para>
    /// This walks every installed plugin, so letting one plugin's failure
    /// escape empties the whole navigation. It is left out and named in the log
    /// instead.
    /// </para>
    /// </summary>
    private IEnumerable<LibraryNavigationEntryDto>? EntriesOf(PluginInfo info, string[] kinds)
    {
        try
        {
            return (pluginManager.GetPluginInstance(info.Id) as IUiPlugin)
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
                .ToList();
        }
        catch (MissingMemberException missing)
        {
            PluginRefusal refusal = PluginRefusalMessages.RemovedContractMember(
                info.Id.ToString(),
                missing.Message
            );

            logger.LogError(
                "Plugin {Plugin} is left out of the navigation. {What} {Why} {Fix}",
                info.Name,
                refusal.What,
                refusal.Why,
                refusal.Fix
            );

            return null;
        }
    }

    [HttpGet("libraries")]
    public async Task<IActionResult> Libraries()
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view libraries");

        List<LibrariesResponseItemDto> response = (
            await libraryRepository.GetSetupLibrariesAsync(userId)
        )
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

        List<Playlist> playlistItems = await musicRepository.GetUserPlaylistsAsync(userId);

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
