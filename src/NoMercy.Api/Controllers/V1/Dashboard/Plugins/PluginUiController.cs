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
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Authorization;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Api.Controllers.V1.Dashboard.Plugins;

/// <summary>
/// The platform's own endpoints for plugin UI, as opposed to the endpoints a
/// plugin owns itself.
/// <para>
/// Authorised for any signed-in user rather than the owner: managing plugins is
/// an owner's job, and using one is not. The owner-only surface stays on
/// <see cref="PluginController"/>.
/// </para>
/// </summary>
[ApiController]
[Tags("Plugin UI")]
[ApiVersion(1.0)]
[Authorize]
public class PluginUiController(IPluginManager pluginManager) : BaseController
{
    /// <summary>
    /// Every plugin the caller's client should show in its navigation.
    /// </summary>
    /// <summary>
    /// Every plugin screen the server can place, grouped by the area it lands in.
    ///
    /// This is what the addons page browses. The client does not work out where
    /// anything goes: the route is resolved here from the mount's kind, so the
    /// web app, a phone and a television all navigate to the same place without
    /// agreeing on anything but the word.
    /// </summary>
    /// <summary>
    /// A plugin's declared pages on one surface, with the shell each wants and
    /// the path already resolved. A plugin declaring none reports none, and its
    /// screens stay reachable through the wildcard.
    /// </summary>
    private List<object> Pages(Ulid pluginId, string kind, string surface)
    {
        if (pluginManager.GetPluginInstance(pluginId) is not IUiPlugin plugin)
            return [];

        string prefix = PluginRoutes.PrefixFor(kind, pluginId).TrimEnd('/');

        return plugin
            .Routes.On(surface)
            .Select(
                object (route) =>
                    new
                    {
                        route.Name,
                        route.Label,
                        Layout = route.LayoutFor(surface),
                        Path = prefix + (route.Path == "/" ? string.Empty : route.Path),
                    }
            )
            .ToList();
    }

    [HttpGet("api/v{version:apiVersion}/plugins/ui/browse")]
    public IActionResult Browse([FromQuery] string? surface)
    {
        string asking = PluginSurface.IsKnown(surface) ? surface! : PluginSurface.Web;

        var groups = pluginManager
            .GetInstalledPlugins()
            .Where(HasUi)
            .SelectMany(info =>
                (pluginManager.GetPluginInstance(info.Id) as IUiPlugin)?.NavEntries.Select(
                    entry => new
                    {
                        PluginId = info.Id,
                        PluginName = info.Name,
                        entry.Label,
                        entry.Icon,
                        Kind = PluginKind.IsKnown(entry.Section)
                            ? entry.Section
                            : PluginKind.Dashboard,
                        entry.Route,
                        // Offered here at all, which is a different question from
                        // what it looks like once opened.
                        AppearsHere = entry.AppearsOn(asking),
                    }
                )
                ?? []
            )
            // A kind the server does not place is dropped rather than listed
            // with a route nothing answers, which would read as a broken plugin.
            .Where(entry => PluginKind.DrawsUi(entry.Kind) && entry.AppearsHere)
            .GroupBy(entry => entry.Kind)
            .OrderBy(group => Array.IndexOf(PluginKind.All, group.Key))
            .Select(group => new
            {
                Kind = group.Key,
                Entries = group
                    .Select(entry => new
                    {
                        entry.PluginId,
                        entry.PluginName,
                        entry.Label,
                        entry.Icon,
                        Path = PluginRoutes.PrefixFor(entry.Kind, entry.PluginId).TrimEnd('/')
                            + (entry.Route == "/" ? string.Empty : entry.Route),
                        // The mount on its own, without this entry's route on
                        // the end. A client that has only the resolved path and
                        // wants to build a sibling route has to guess at where
                        // the mount stops, and guessing wrong asks the plugin
                        // for a page nobody declared.
                        Prefix = PluginRoutes.PrefixFor(entry.Kind, entry.PluginId).TrimEnd('/'),
                        // Every page the plugin declares, so a client registers a
                        // named route for each when a server is chosen rather than
                        // discovering them one navigation at a time.
                        Pages = Pages(entry.PluginId, entry.Kind, asking),
                    })
                    .OrderBy(entry => entry.PluginName)
                    .ToList(),
            })
            .ToList();

        return Ok(new DataResponseDto<object> { Data = groups });
    }

    [HttpGet("api/v{version:apiVersion}/plugins/ui")]
    public IActionResult Discover()
    {
        List<PluginUiDescriptorDto> descriptors = pluginManager
            .GetInstalledPlugins()
            .Where(HasUi)
            .Select(info =>
                PluginUiDescriptorDto.From(
                    info,
                    pluginManager.GetPluginInstance(info.Id) as IUiPlugin
                )
            )
            .ToList();

        return Ok(new DataResponseDto<IEnumerable<PluginUiDescriptorDto>> { Data = descriptors });
    }

    /// <summary>
    /// What a plugin wants rendered for one of its routes.
    /// </summary>
    /// <summary>
    /// The plugin's own strings for one locale.
    ///
    /// A plugin supplies the text in the components it builds, so the client has
    /// no way to translate what it has never seen. Serving them under the
    /// plugin's id lets the client merge them into its own catalogue as a
    /// namespace, which is what keeps two plugins using the same key apart.
    /// </summary>
    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/translations/{locale}")]
    public async Task<IActionResult> Translations(Ulid id, string locale, CancellationToken ct)
    {
        PluginInfo? info = pluginManager.GetPluginInfo(id);

        if (info is null)
            return NotFoundResponse("Plugin not found");

        // The manager owns the fallback because it is the thing holding the
        // manifest: a viewer whose language the plugin does not ship reads it in
        // the language it was written in, never in empty labels.
        Dictionary<string, string>? strings = await pluginManager.ReadTranslationsAsync(
            id,
            locale,
            ct
        );

        return Ok(new DataResponseDto<Dictionary<string, string>> { Data = strings ?? [] });
    }

    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/view")]
    public async Task<IActionResult> View(
        Ulid id,
        [FromQuery] string? route,
        [FromQuery] string? surface,
        CancellationToken ct
    )
    {
        PluginInfo? info = pluginManager.GetPluginInfo(id);

        if (info is null || !HasUi(info))
            return NotFoundResponse("Plugin not found");

        if (pluginManager.GetPluginInstance(id) is not IUiPlugin plugin)
            return NotFoundResponse("Plugin provides no UI");

        PluginViewRequest request = new()
        {
            Route = string.IsNullOrWhiteSpace(route) ? "/" : route,
            Query = Request
                .Query.Where(entry => entry.Key != "route" && entry.Key != "surface")
                .ToDictionary(entry => entry.Key, entry => entry.Value.ToString()),
            Caller = new PluginCaller(
                new UserId(new Ulid(User.UserId())),
                User.UserName(),
                User.Role() == "owner" ? PluginRole.Owner : PluginRole.Member,
                PluginAccess.Owned,
                Request.Headers.AcceptLanguage.ToString() is { Length: > 0 } locale ? locale : "en",
                PluginSurface.IsKnown(surface) ? surface! : PluginSurface.Web
            ),
            // An unknown surface falls back rather than being passed through. A
            // plugin branching on it would hit its own default and serve the
            // desktop shape to a television, which looks like a plugin bug.
            Surface = PluginSurface.IsKnown(surface) ? surface! : PluginSurface.Web,
        };

        try
        {
            PluginView view = await plugin.GetViewAsync(request, ct);

            // A declared route names the shell its page wants, and the plugin
            // should not have to repeat it on every view it builds. A view that
            // named one itself keeps it.
            PluginRouteMatch? declared = plugin.Routes.Resolve(request.Route);

            if (declared is not null && view.Layout == PluginLayout.Standard)
                view.Layout = declared.Route.LayoutFor(request.Surface);

            return Ok(new DataResponseDto<PluginView> { Data = view });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A plugin throwing while building its own screen is that plugin's
            // failure, not a server error, and the client should render an
            // empty panel rather than a 500 the user reads as the server
            // breaking.
            return BadRequestResponse($"Plugin could not render this view: {exception.Message}");
        }
    }

    private static bool HasUi(PluginInfo info) =>
        info.Status == PluginStatus.Active
        && PluginCapabilityGuard.DeclaresHook(info.Capabilities, PluginHookCapability.Ui);
}
