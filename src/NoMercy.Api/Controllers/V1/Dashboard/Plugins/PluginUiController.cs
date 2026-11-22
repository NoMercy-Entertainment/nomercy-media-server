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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.Plugins;
using NoMercy.Authorization;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Capabilities;

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
public class PluginUiController(
    IPluginManager pluginManager,
    IPluginAccessResolver accessResolver,
    ILogger<PluginUiController> logger
) : BaseController
{
    /// <summary>
    /// What this caller may see. One resolver for the listings and for the
    /// page, so nobody is offered something that then refuses them.
    /// </summary>
    private PluginAccess AccessTo(Ulid pluginId) => accessResolver.Resolve(pluginId, User.UserId());

    private PluginCaller CallerFor(PluginAccess access, string? surface) =>
        PluginCallers.For(
            User,
            access,
            Request.Headers.AcceptLanguage.ToString(),
            surface,
            accessResolver
        );

    private JsonSerializerSettings JsonSettings() =>
        HttpContext
            .RequestServices?.GetService<IOptions<MvcNewtonsoftJsonOptions>>()
            ?.Value.SerializerSettings
        ?? new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Converters = [new StringEnumConverter()],
        };

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
    private List<object> Pages(Ulid pluginId, string kind, string surface, PluginCaller viewer)
    {
        if (pluginManager.GetPluginInstance(pluginId) is not IUiPlugin plugin)
            return [];

        string prefix = PluginRoutes.PrefixFor(kind, pluginId).TrimEnd('/');

        return plugin
            .Routes.On(surface)
            .Where(route => viewer.Admits(route.Access))
            .Select(
                object (route) =>
                    new
                    {
                        route.Name,
                        route.Label,
                        Layout = route.LayoutFor(surface),
                        route.Chrome,
                        Path = prefix + (route.Path == "/" ? string.Empty : route.Path),
                    }
            )
            .ToList();
    }

    [HttpGet("api/v{version:apiVersion}/plugins/ui/browse")]
    public IActionResult Browse([FromQuery] string? surface)
    {
        string asking = PluginSurface.IsKnown(surface) ? surface! : PluginSurface.Web;
        PluginCaller viewer = CallerFor(PluginAccess.Shared, asking);

        var groups = pluginManager
            .GetInstalledPlugins()
            .Where(HasUi)
            .Where(info => AccessTo(info.Id) != PluginAccess.None)
            .SelectMany(info =>
                WithoutStalePlugins(
                    info,
                    () =>
                        (pluginManager.GetPluginInstance(info.Id) as IUiPlugin)
                            ?.NavEntries.Where(entry => viewer.Admits(entry.Access))
                            .Select(entry => new
                            {
                                PluginId = info.Id,
                                PluginName = info.Name,
                                entry.Label,
                                entry.Icon,
                                Kind = PluginKind.IsKnown(entry.Section)
                                    ? entry.Section
                                    : PluginKind.Dashboard,
                                entry.Route,
                                // Where inside the kind it draws. A client
                                // reserves the space for a slot before any
                                // plugin is seen, so browsing has to say which
                                // one each entry asked for.
                                entry.Slot,
                                Access = entry.Access.ToString().ToLowerInvariant(),
                                // Offered here at all, which is a different question from
                                // what it looks like once opened.
                                AppearsHere = entry.AppearsOn(asking),
                            })
                ) ?? []
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
                        entry.Slot,
                        entry.Access,
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
                        Pages = Pages(entry.PluginId, entry.Kind, asking, viewer),
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
        bool seesOwnerEntries = CallerFor(PluginAccess.Shared, null)
            .Admits(PluginRouteAccess.Owner);

        List<PluginUiDescriptorDto> descriptors = pluginManager
            .GetInstalledPlugins()
            .Where(HasUi)
            .Select(info => (Info: info, Access: AccessTo(info.Id)))
            .Where(entry => entry.Access != PluginAccess.None)
            .Select(entry =>
                WithoutStalePlugins(
                    entry.Info,
                    () =>
                        PluginUiDescriptorDto.From(
                            entry.Info,
                            pluginManager.GetPluginInstance(entry.Info.Id) as IUiPlugin,
                            entry.Access
                        )
                )
            )
            .OfType<PluginUiDescriptorDto>()
            .Select(descriptor =>
                descriptor with
                {
                    NavEntries = descriptor
                        .NavEntries.Where(entry => seesOwnerEntries || entry.Access != "owner")
                        .ToList(),
                }
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
    public Task<IActionResult> View(
        Ulid id,
        [FromQuery] string? route,
        [FromQuery] string? surface,
        CancellationToken ct
    ) => Render(id, route, surface, input: null, ct);

    /// <summary>
    /// The same view, with what the caller typed.
    /// <para>
    /// A sibling rather than a flag on the GET: a form submission has a body,
    /// and a GET carrying one is a request caches and proxies are entitled to
    /// treat as the plain page.
    /// </para>
    /// </summary>
    [HttpPost("api/v{version:apiVersion}/plugins/{id:ulid}/view")]
    public Task<IActionResult> Submit(
        Ulid id,
        [FromQuery] string? route,
        [FromQuery] string? surface,
        [FromBody] PluginViewInputDto? input,
        CancellationToken ct
    ) => Render(id, route, surface, input, ct);

    private async Task<IActionResult> Render(
        Ulid id,
        string? route,
        string? surface,
        PluginViewInputDto? input,
        CancellationToken ct
    )
    {
        PluginInfo? info = pluginManager.GetPluginInfo(id);

        if (info is null || !HasUi(info))
            return NotFoundResponse("Plugin not found");

        // Before the plugin is asked for anything: a page that renders and
        // then refuses has already told the caller what is installed here.
        PluginAccess access = AccessTo(id);
        if (access == PluginAccess.None)
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        if (pluginManager.GetPluginInstance(id) is not IUiPlugin plugin)
            return NotFoundResponse("Plugin provides no UI");

        PluginCaller caller = CallerFor(access, surface);
        bool isOwner = caller.Admits(PluginRouteAccess.Owner);
        string asked = string.IsNullOrWhiteSpace(route) ? "/" : route;

        PluginRouteMatch? declared = plugin.Routes.Resolve(asked);

        if (declared is not null && !caller.Admits(declared.Route.Access))
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        if (declared is null && !isOwner && NamesOwnerNavEntry(plugin, asked))
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        PluginViewRequest request = new()
        {
            Route = asked,
            Query = Request
                .Query.Where(entry => entry.Key != "route" && entry.Key != "surface")
                .ToDictionary(entry => entry.Key, entry => entry.Value.ToString()),
            Caller = caller,
            // An unknown surface falls back rather than being passed through. A
            // plugin branching on it would hit its own default and serve the
            // desktop shape to a television, which looks like a plugin bug.
            Surface = PluginSurface.IsKnown(surface) ? surface! : PluginSurface.Web,
            // Empty for a page that was simply opened, so a plugin written
            // before forms existed behaves exactly as it did.
            Values = input?.Values ?? new Dictionary<string, object?>(),
            Action = input?.Action,
            // The address this viewer reached, so a plugin minting a link back
            // to itself uses the hostname that works for this caller rather
            // than one read from configuration.
            Origin = $"{Request.Scheme}://{Request.Host}",
        };

        if (!isOwner && input is not null && !await MayPost(plugin, request, input, ct))
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        try
        {
            PluginView view = await plugin.GetViewAsync(request, ct);

            // Checked on the server as well as in every client. A client's own
            // allow-list is the first line; this one holds for a client that
            // has not caught up, and for one nobody has written yet.
            string? forbidden = PluginViewComponentGuard.FindForbidden(view);
            if (forbidden is not null)
            {
                PluginRefusal refusal = PluginRefusalMessages.ViewComponentForbidden(
                    id.ToString(),
                    request.Route,
                    forbidden
                );

                logger.LogError(
                    "Plugin {Plugin} view was not sent: {What} {Why}",
                    info.Name,
                    refusal.What,
                    refusal.Why
                );

                return StatusCode(
                    StatusCodes.Status422UnprocessableEntity,
                    PluginRefusalDto.From(refusal)
                );
            }

            // The paging caps hold on the server too, so no plugin can send an
            // unbounded shelf. An append is answered with its one shelf.
            view = PluginShelfCaps.Apply(view, request.Page, out int trimmed);
            if (trimmed > 0)
                logger.LogWarning(
                    "Plugin {Plugin} sent {Trimmed} shelf items past the paging caps on {Route}; they were left out",
                    info.Name,
                    trimmed,
                    request.Route
                );

            // A declared route names the shell its page wants, and the plugin
            // should not have to repeat it on every view it builds. A view that
            // named one itself keeps it.
            if (declared is not null && view.Layout == PluginLayout.Standard)
                view.Layout = declared.Route.LayoutFor(request.Surface);

            if (!isOwner && PluginViewActionGuard.Strip(view, JsonSettings(), out JToken stripped))
                return Ok(new DataResponseDto<JToken> { Data = stripped });

            return Ok(new DataResponseDto<PluginView> { Data = view });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (MissingMemberException missing)
        {
            // A plugin built against contract v2 reaching a member v3 took away.
            // The runtime raises this when the method is prepared, so it lands
            // here rather than inside whatever the plugin wrapped in a try. The
            // author needs the member named and the version to rebuild against,
            // which the raw message does not carry.
            PluginRefusal refusal = PluginRefusalMessages.RemovedContractMember(
                id.ToString(),
                missing.Message
            );

            return StatusCode(501, PluginRefusalDto.From(refusal));
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

    /// <summary>Whether an owner-only nav entry of the plugin points at this route.</summary>
    private static bool NamesOwnerNavEntry(IUiPlugin plugin, string route) =>
        plugin.NavEntries?.Any(entry =>
            entry.Access == PluginRouteAccess.Owner
            && string.Equals(
                entry.Route.TrimEnd('/'),
                route.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase
            )
        ) == true;

    /// <summary>Whether a non-owner may post this: a type an owner action shares cannot be told apart, so it is refused.</summary>
    private async Task<bool> MayPost(
        IUiPlugin plugin,
        PluginViewRequest request,
        PluginViewInputDto input,
        CancellationToken ct
    )
    {
        bool hasAction = input.Action is { Length: > 0 };
        if (!hasAction && input.Values is not { Count: > 0 })
            return true;

        try
        {
            PluginView view = await plugin.GetViewAsync(
                new()
                {
                    Route = request.Route,
                    Query = request.Query,
                    Caller = request.Caller,
                    Surface = request.Surface,
                    Origin = request.Origin,
                },
                ct
            );

            JToken offered = PluginViewActionGuard.Read(view, JsonSettings());

            return hasAction
                ? PluginViewActionGuard.OffersOnlySharedAction(offered, input.Action!)
                : !PluginViewActionGuard.HoldsOwnerAction(offered, "submitForm");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads something from a plugin, and answers null when that plugin was
    /// built against a contract member v3 took away.
    /// <para>
    /// These endpoints walk every installed plugin. Letting one plugin's
    /// missing member escape takes the whole list down, so the addons page goes
    /// blank because a single plugin is out of date. It is skipped and named in
    /// the log instead, with the refusal of design section 3.9.
    /// </para>
    /// </summary>
    private T? WithoutStalePlugins<T>(PluginInfo info, Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (MissingMemberException missing)
        {
            PluginRefusal refusal = PluginRefusalMessages.RemovedContractMember(
                info.Id.ToString(),
                missing.Message
            );

            logger.LogError(
                "Plugin {Plugin} is not listed: {What} {Why} {Fix}",
                info.Name,
                refusal.What,
                refusal.Why,
                refusal.Fix
            );

            return default;
        }
    }

    private static bool HasUi(PluginInfo info) =>
        info.Status == PluginStatus.Active
        && PluginCapabilityGuard.DeclaresHook(info.Capabilities, PluginHookCapability.Ui);
}
