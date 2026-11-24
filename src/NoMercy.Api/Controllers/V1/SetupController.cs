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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;

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
    IPluginAccessResolver accessResolver,
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

        string locale = PluginLocale();

        List<LibraryNavigationEntryDto> entries = LibraryNavigation.Build(
            libraries,
            hasAnime,
            await PluginEntries(locale, ct, PluginKind.Library, PluginKind.Video),
            await PluginEntries(locale, ct, PluginKind.Music)
        );

        return Ok(new DataResponseDto<List<LibraryNavigationEntryDto>> { Data = entries });
    }

    /// <summary>
    /// The pages plugins mount into this section. A plugin awaiting consent or
    /// disabled has no instance, so it contributes nothing — the entry appears
    /// the moment it is enabled and disappears again when it is not.
    /// </summary>
    private async Task<List<LibraryNavigationEntryDto>> PluginEntries(
        string locale,
        CancellationToken ct,
        params string[] kinds
    )
    {
        List<LibraryNavigationEntryDto> entries = [];

        foreach (PluginInfo info in pluginManager.GetInstalledPlugins())
        {
            List<LibraryNavigationEntryDto> own = [.. EntriesOf(info, kinds) ?? []];
            if (own.Count == 0)
                continue;

            Dictionary<string, string>? strings = await TranslationsOf(info, locale, ct);
            entries.AddRange(
                own.Select(entry => entry with { Label = Translate(entry.Label, strings) })
            );
        }

        return [.. entries.OrderBy(entry => entry.Label)];
    }

    /// <summary>
    /// A plugin's nav label is a key into its own <c>lang/&lt;locale&gt;.json</c>,
    /// not text. The client only knows its own catalogue, so a key sent as it is
    /// reaches the sidebar as <c>stations.title</c>. A key the plugin has no
    /// string for is sent unchanged, which is also what a plugin that wrote
    /// plain text as its label has always got.
    /// </summary>
    private static string Translate(string label, Dictionary<string, string>? strings) =>
        strings is not null
        && strings.TryGetValue(label, out string? text)
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : label;

    /// <summary>
    /// The plugin's strings in the caller's language, or none. A bundle that
    /// cannot be read costs that plugin its translations, not the navigation:
    /// its entries keep their keys.
    /// </summary>
    private async Task<Dictionary<string, string>?> TranslationsOf(
        PluginInfo info,
        string locale,
        CancellationToken ct
    )
    {
        try
        {
            return await pluginManager.ReadTranslationsAsync(info.Id, locale, ct);
        }
        // Any failure, not only I/O: the storage layer refuses a path with an
        // InvalidOperationException (output.path_not_allowed), and one plugin's
        // bundle must never take the navigation of every user down with it.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Plugin {Plugin} navigation labels are left untranslated: {Reason}",
                info.Name,
                exception.Message
            );

            return null;
        }
    }

    /// <summary>
    /// The caller's language as a plugin names its locales: <c>nl</c>, not
    /// <c>nl-NL</c>. A language the plugin does not ship falls back to the one
    /// it was written in, so anything unrecognised here still reads sensibly.
    /// </summary>
    private string PluginLocale()
    {
        string language = Language() ?? string.Empty;
        string tag = language.Split(['-', '_', ',', ';'])[0].Trim().ToLowerInvariant();

        return tag.Length > 0 ? tag : "en";
    }

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
            bool isOwner = accessResolver.IsOwner(User.UserId());

            return (pluginManager.GetPluginInstance(info.Id) as IUiPlugin)
                ?.NavEntries.Where(entry => kinds.Contains(entry.Section))
                .Where(entry => isOwner || entry.Access != PluginRouteAccess.Owner)
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

        bool moderator = AuthPolicy.IsModerator(User);
        List<LibrariesResponseItemDto> response = (
            await libraryRepository.GetSetupLibrariesAsync(userId)
        )
            .Select(library => new LibrariesResponseItemDto(library))
            .Select(item => moderator ? item : item.WithoutServerDetails())
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
        var permissions = new
        {
            owner = AuthPolicy.IsOwner(User),
            manager = AuthPolicy.IsModerator(User),
            allowed = AuthPolicy.IsAllowed(User),
            optical_access = AuthPolicy.IsOpticalAccess(User),
        };
        // Android 2026.11.0 and older read {data:{...}}; newer clients read the bare fields.
        return Ok(
            new
            {
                permissions.owner,
                permissions.manager,
                permissions.allowed,
                permissions.optical_access,
                data = permissions,
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
