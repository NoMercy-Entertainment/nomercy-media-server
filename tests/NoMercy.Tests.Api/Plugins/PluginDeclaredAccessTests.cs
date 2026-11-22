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

using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Api.Hubs;
using NoMercy.Api.Plugins;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Networking.Messaging;
using NoMercy.Plugin.Samples.Dashboard;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Entitlements;
using NoMercy.PluginSdk.Hub;
using NoMercy.PluginSdk.Lan;
using NoMercy.PluginSdk.Mvc;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// Access a plugin declares is enforced by the server: on its REST actions, its
/// pages, its nav entries, the actions inside its views and its hub methods.
/// Every test calls the real entry point with a member and with the owner.
/// </summary>
[Trait("Category", "Unit")]
public class PluginDeclaredAccessTests
{
    private static readonly Ulid PluginId = Ulid.Parse("01J9ZK5V8Y000000000000000D");
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid MemberId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static PluginAccessResolver Resolver()
    {
        Mock<IPluginInstallFacts> facts = new();
        facts.Setup(one => one.TierOf(It.IsAny<Ulid>())).Returns(PluginTier.Free);
        Mock<IPluginEntitlementStore> store = new();
        store
            .SetupGet(one => one.Current)
            .Returns(new PluginEntitlementBundle(Ulid.Empty, Now.AddDays(-1), Now.AddDays(1), []));
        Mock<IPluginMembership> membership = new();
        membership.Setup(one => one.IsAcceptedMember(MemberId)).Returns(true);

        return new(
            facts.Object,
            store.Object,
            membership.Object,
            TimeProvider.System,
            () => OwnerId
        );
    }

    private static ClaimsPrincipal User(Guid id, string? role = null)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, id.ToString())];
        if (role is not null)
            claims.Add(new(ClaimTypes.Role, role));

        return new(new ClaimsIdentity(claims, "test"));
    }

    private static PluginInfo Info(bool rest = false, bool ws = false, bool anonymous = false) =>
        new()
        {
            Id = PluginId,
            Name = "Declared Access",
            Description = "A plugin that declares owner access",
            Version = new Version(1, 0, 0),
            Status = PluginStatus.Active,
            Capabilities = new()
            {
                Hooks = ["ui"],
                Rest = rest,
                RestAnonymous = anonymous,
                Ws = ws,
            },
        };

    // --- REST (decision 2) ---------------------------------------------------

    [PluginRequires(PluginRouteAccess.Owner)]
    private sealed class OwnerOnlyController : PluginControllerBase
    {
        public IActionResult Wipe() => Ok();

        [PluginRequires(PluginRouteAccess.Shared)]
        public IActionResult Open() => Ok();
    }

    private sealed class SharedController : PluginControllerBase
    {
        [PluginRequires(PluginRouteAccess.Owner)]
        public IActionResult Wipe() => Ok();

        public IActionResult Open() => Ok();
    }

    private static async Task<(ActionExecutingContext Context, bool Reached)> Rest(
        PluginControllerBase controller,
        string method,
        ClaimsPrincipal user,
        bool anonymous = false,
        bool lan = false
    )
    {
        Mock<IPluginManager> manager = new();
        manager
            .Setup(one => one.GetPluginInfo(PluginId))
            .Returns(Info(rest: true, anonymous: anonymous));

        RouteData route = new();
        route.Values["pluginId"] = PluginId.ToString();
        DefaultHttpContext http = new() { User = user };
        if (lan)
            http.Features.Set(new PluginLanDispatch(PluginId, "device-1", "Living room"));

        // Controller attributes come before the action's, as the endpoint lists them.
        List<object> metadata = [];
        metadata.AddRange(controller.GetType().GetCustomAttributes(true));
        metadata.AddRange(
            controller.GetType().GetMethod(method)!.GetCustomAttributes<PluginRequiresAttribute>()
        );

        ActionExecutingContext context = new(
            new ActionContext(http, route, new ActionDescriptor { EndpointMetadata = metadata }),
            [],
            new Dictionary<string, object?>(),
            controller
        );

        bool reached = false;
        await new PluginControllerCapabilityFilter(
            manager.Object,
            Resolver()
        ).OnActionExecutionAsync(
            context,
            () =>
            {
                reached = true;
                return Task.FromResult(new ActionExecutedContext(context, [], controller));
            }
        );

        return (context, reached);
    }

    [Fact]
    public async Task Rest_member_is_refused_an_action_the_plugin_marks_owner_only()
    {
        (ActionExecutingContext context, bool reached) = await Rest(
            new SharedController(),
            nameof(SharedController.Wipe),
            User(MemberId)
        );

        reached.Should().BeFalse();
        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Rest_owner_reaches_an_owner_only_action_and_the_plugin_reads_the_caller()
    {
        (ActionExecutingContext context, bool reached) = await Rest(
            new SharedController(),
            nameof(SharedController.Wipe),
            User(OwnerId)
        );

        reached.Should().BeTrue();
        context
            .HttpContext.Items[PluginControllerBase.CallerItemKey]
            .Should()
            .BeOfType<PluginCaller>()
            .Which.Role.Should()
            .Be(PluginRole.Owner);
    }

    [Fact]
    public async Task Rest_member_reaches_a_shared_action_with_a_member_caller()
    {
        (ActionExecutingContext context, bool reached) = await Rest(
            new SharedController(),
            nameof(SharedController.Open),
            User(MemberId)
        );

        reached.Should().BeTrue();
        context
            .HttpContext.Items[PluginControllerBase.CallerItemKey]
            .Should()
            .BeOfType<PluginCaller>()
            .Which.Role.Should()
            .Be(PluginRole.Member);
    }

    [Fact]
    public async Task Rest_class_level_owner_access_binds_every_action_without_its_own()
    {
        (_, bool wipeReached) = await Rest(
            new OwnerOnlyController(),
            nameof(OwnerOnlyController.Wipe),
            User(MemberId)
        );
        (_, bool openReached) = await Rest(
            new OwnerOnlyController(),
            nameof(OwnerOnlyController.Open),
            User(MemberId)
        );

        wipeReached.Should().BeFalse("the class asks for owner access");
        openReached.Should().BeTrue("the method asked for shared access itself");
    }

    [Fact]
    public async Task Rest_a_role_claim_alone_does_not_make_a_member_the_owner()
    {
        (_, bool reached) = await Rest(
            new SharedController(),
            nameof(SharedController.Wipe),
            User(MemberId, role: "owner")
        );

        reached.Should().BeFalse("the server's owner is the one the access resolver holds");
    }

    [Fact]
    public async Task Rest_lan_device_is_refused_an_owner_only_action()
    {
        (ActionExecutingContext context, bool reached) = await Rest(
            new SharedController(),
            nameof(SharedController.Wipe),
            new ClaimsPrincipal(),
            lan: true
        );

        reached.Should().BeFalse();
        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Rest_lan_device_still_reaches_an_action_without_an_owner_requirement()
    {
        (_, bool reached) = await Rest(
            new SharedController(),
            nameof(SharedController.Open),
            new ClaimsPrincipal(),
            lan: true
        );

        reached.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rest_open_surface_refuses_an_owner_only_action_for_everyone_but_the_owner(
        bool signedInMember
    )
    {
        ClaimsPrincipal caller = signedInMember ? User(MemberId) : new ClaimsPrincipal();

        (ActionExecutingContext context, bool reached) = await Rest(
            new SharedController(),
            nameof(SharedController.Wipe),
            caller,
            anonymous: true
        );

        reached.Should().BeFalse();
        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Rest_open_surface_admits_the_owner_to_an_owner_only_action()
    {
        (_, bool reached) = await Rest(
            new SharedController(),
            nameof(SharedController.Wipe),
            User(OwnerId),
            anonymous: true
        );

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Rest_open_surface_keeps_an_action_without_an_owner_requirement_open()
    {
        (_, bool reached) = await Rest(
            new SharedController(),
            nameof(SharedController.Open),
            new ClaimsPrincipal(),
            anonymous: true
        );

        reached.Should().BeTrue();
    }

    // --- Page routes, nav, view actions (decisions 3, 4, 5) -----------------

    private static PluginUiController Controller(
        IUiPlugin plugin,
        ClaimsPrincipal user,
        IPluginAccessResolver? resolver = null
    )
    {
        Mock<IPluginManager> manager = new();
        PluginInfo info = Info();
        manager.Setup(one => one.GetPluginInfo(PluginId)).Returns(info);
        manager.Setup(one => one.GetInstalledPlugins()).Returns([info]);
        manager.Setup(one => one.GetPluginInstance(PluginId)).Returns(plugin);

        return new(manager.Object, resolver ?? Resolver(), NullLogger<PluginUiController>.Instance)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { User = user } },
        };
    }

    private static Mock<IUiPlugin> Plugin(
        PluginRouteTable? routes = null,
        IReadOnlyList<PluginNavEntry>? nav = null,
        Func<PluginViewRequest, PluginView>? view = null
    )
    {
        Mock<IUiPlugin> plugin = new();
        plugin.SetupGet(one => one.Routes).Returns(routes ?? new PluginRouteTable());
        plugin.SetupGet(one => one.NavEntries).Returns(nav ?? []);
        plugin
            .Setup(one =>
                one.GetViewAsync(It.IsAny<PluginViewRequest>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                (PluginViewRequest request, CancellationToken _) =>
                    view?.Invoke(request) ?? new PluginView()
            );
        return plugin;
    }

    private static string Json(IActionResult result) =>
        JsonConvert.SerializeObject(((ObjectResult)result).Value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Page_member_is_refused_a_route_the_plugin_marks_owner_only(bool submit)
    {
        PluginRouteTable routes = new(
            new PluginRoute
            {
                Name = "admin",
                Path = "/admin",
                Access = PluginRouteAccess.Owner,
            }
        );
        Mock<IUiPlugin> plugin = Plugin(routes);
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        IActionResult result = submit
            ? await member.Submit(PluginId, "/admin", null, null, default)
            : await member.View(PluginId, "/admin", null, default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        plugin.Verify(
            one => one.GetViewAsync(It.IsAny<PluginViewRequest>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Page_owner_opens_a_route_the_plugin_marks_owner_only()
    {
        PluginRouteTable routes = new(
            new PluginRoute
            {
                Name = "admin",
                Path = "/admin",
                Access = PluginRouteAccess.Owner,
            }
        );
        PluginUiController owner = Controller(Plugin(routes).Object, User(OwnerId));

        IActionResult result = await owner.View(PluginId, "/admin", null, default);

        result.Should().BeOfType<OkObjectResult>();
    }

    private static readonly IReadOnlyList<PluginNavEntry> NavEntries =
    [
        new()
        {
            Section = PluginKind.Dashboard,
            Label = "shared-entry",
            Route = "/",
        },
        new()
        {
            Section = PluginKind.Dashboard,
            Label = "owner-entry",
            Route = "/admin",
            Access = PluginRouteAccess.Owner,
        },
    ];

    [Fact]
    public void Nav_browse_leaves_an_owner_entry_out_for_a_member()
    {
        PluginUiController member = Controller(Plugin(nav: NavEntries).Object, User(MemberId));

        string json = Json(member.Browse(null));

        json.Should().Contain("shared-entry");
        json.Should().NotContain("owner-entry");
    }

    [Fact]
    public void Nav_browse_lists_an_owner_entry_for_the_owner()
    {
        PluginUiController owner = Controller(Plugin(nav: NavEntries).Object, User(OwnerId));

        Json(owner.Browse(null)).Should().Contain("owner-entry");
    }

    [Fact]
    public void Nav_discover_leaves_an_owner_entry_out_for_a_member()
    {
        PluginUiController member = Controller(Plugin(nav: NavEntries).Object, User(MemberId));

        string json = Json(member.Discover());

        json.Should().Contain("shared-entry");
        json.Should().NotContain("owner-entry");
    }

    private static PluginActionIntent Intent(
        string type,
        string marker,
        PluginRouteAccess access
    ) =>
        new()
        {
            Type = type,
            Payload = new() { ["marker"] = marker },
            Access = access,
        };

    private static PluginView ActionsView(PluginRouteAccess wipeAccess) =>
        new()
        {
            Components =
            [
                new()
                {
                    Id = "wipe",
                    Component = "NMButton",
                    Action = Intent("callPlugin", "wipe-everything", wipeAccess),
                },
                new()
                {
                    Id = "save",
                    Component = "NMButton",
                    Action = Intent("submitForm", "save-settings", PluginRouteAccess.Shared),
                },
            ],
        };

    [Fact]
    public async Task View_member_never_receives_an_action_the_plugin_marks_owner_only()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ => ActionsView(PluginRouteAccess.Owner));
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        string json = Json(await member.View(PluginId, "/", null, default));

        json.Should().Contain("save-settings");
        json.Should().NotContain("wipe-everything");
    }

    [Fact]
    public async Task View_owner_keeps_every_action()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ => ActionsView(PluginRouteAccess.Owner));
        PluginUiController owner = Controller(plugin.Object, User(OwnerId));

        string json = Json(await owner.View(PluginId, "/", null, default));

        json.Should().Contain("save-settings").And.Contain("wipe-everything");
    }

    private static Task<IActionResult> Post(PluginUiController controller, string action) =>
        controller.Submit(
            PluginId,
            "/",
            null,
            new()
            {
                Action = action,
                Values = new() { ["field"] = "value" },
            },
            default
        );

    [Fact]
    public async Task Post_member_cannot_run_an_owner_only_action_by_name()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ => ActionsView(PluginRouteAccess.Owner));
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        IActionResult result = await Post(member, "callPlugin");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        plugin.Verify(
            one =>
                one.GetViewAsync(
                    It.Is<PluginViewRequest>(request => request.Action == "callPlugin"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task Post_member_can_run_a_shared_action_the_view_holds()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ => ActionsView(PluginRouteAccess.Owner));
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        IActionResult result = await Post(member, "submitForm");

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Post_member_is_refused_when_the_check_render_fails()
    {
        Mock<IUiPlugin> plugin = Plugin(view: request =>
            request.Action is null
                ? throw new InvalidOperationException("no view without an action")
                : ActionsView(PluginRouteAccess.Shared)
        );
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        IActionResult result = await Post(member, "submitForm");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Post_owner_skips_the_check_render()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ => ActionsView(PluginRouteAccess.Owner));
        PluginUiController owner = Controller(plugin.Object, User(OwnerId));

        IActionResult result = await Post(owner, "callPlugin");

        result.Should().BeOfType<OkObjectResult>();
        plugin.Verify(
            one => one.GetViewAsync(It.IsAny<PluginViewRequest>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/details/42")]
    [InlineData("/settings")]
    public async Task Sample_every_shared_action_in_the_dashboard_views_still_passes_for_a_member(
        string route
    )
    {
        DashboardSamplePlugin sample = new();
        PluginView view = await sample.GetViewAsync(
            new() { Route = route, Caller = MemberCaller() },
            default
        );
        List<string> actions = ActionTypes(view);

        PluginUiController member = Controller(sample, User(MemberId), Resolver());
        foreach (string action in actions)
        {
            IActionResult result = await member.Submit(
                PluginId,
                route,
                null,
                new() { Action = action },
                default
            );

            result.Should().BeOfType<OkObjectResult>($"{action} is shared on {route}");
        }

        if (route != "/settings")
            actions.Should().NotBeEmpty("the sample's pages hold navigation actions");
    }

    private static PluginCaller MemberCaller() =>
        new(
            new UserId(new Ulid(MemberId)),
            "Sam",
            PluginRole.Member,
            PluginAccess.Shared,
            "en",
            PluginSurface.Web
        );

    private static List<string> ActionTypes(PluginView view) =>
        JsonConvert
            .DeserializeObject<Newtonsoft.Json.Linq.JToken>(JsonConvert.SerializeObject(view))!
            .SelectTokens("$..*")
            .OfType<Newtonsoft.Json.Linq.JObject>()
            .Where(node =>
                node.Properties()
                    .Any(p => string.Equals(p.Name, "payload", StringComparison.OrdinalIgnoreCase))
            )
            .Select(node =>
                node.Properties()
                    .First(p => string.Equals(p.Name, "type", StringComparison.OrdinalIgnoreCase))
                    .Value.ToString()
            )
            .Distinct()
            .ToList();

    // --- Posted action, action shape, undeclared owner route ----------------

    private static PluginView TwoFormsView(PluginRouteAccess second) =>
        new()
        {
            Components =
            [
                new()
                {
                    Id = "profile",
                    Component = "NMButton",
                    Action = Intent("submitForm", "profile-form", PluginRouteAccess.Shared),
                },
                new()
                {
                    Id = "admin",
                    Component = "NMButton",
                    Action = Intent("submitForm", "admin-form", second),
                },
            ],
        };

    private static Task<IActionResult> PostValues(PluginUiController controller, string? action) =>
        controller.Submit(
            PluginId,
            "/",
            null,
            new()
            {
                Action = action,
                Values = new() { ["field"] = "value" },
            },
            default
        );

    [Fact]
    public async Task Post_member_cannot_run_an_owner_form_that_shares_its_type_with_a_shared_one()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ => TwoFormsView(PluginRouteAccess.Owner));
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        IActionResult result = await Post(member, "submitForm");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        plugin.Verify(
            one =>
                one.GetViewAsync(
                    It.Is<PluginViewRequest>(request => request.Action == "submitForm"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task Post_member_can_run_a_type_whose_actions_are_all_shared()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ => TwoFormsView(PluginRouteAccess.Shared));
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        IActionResult result = await Post(member, "submitForm");

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Post_member_with_values_and_no_action_is_refused_when_the_view_holds_an_owner_form()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ => TwoFormsView(PluginRouteAccess.Owner));
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        IActionResult result = await PostValues(member, null);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        plugin.Verify(
            one =>
                one.GetViewAsync(
                    It.Is<PluginViewRequest>(request => request.Values.Count > 0),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task Post_member_with_values_and_no_action_passes_when_the_view_holds_no_owner_form()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ => TwoFormsView(PluginRouteAccess.Shared));
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        IActionResult result = await PostValues(member, null);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task View_member_never_receives_a_hand_written_owner_action_without_a_payload()
    {
        Mock<IUiPlugin> plugin = Plugin(view: _ =>
            new()
            {
                Components =
                [
                    new()
                    {
                        Id = "hand",
                        Component = "NMButton",
                        Props = new()
                        {
                            ["onPress"] = new Dictionary<string, object?>
                            {
                                ["type"] = "callPlugin",
                                ["access"] = "owner",
                                ["target"] = "hand-written-wipe",
                            },
                        },
                    },
                ],
            }
        );
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        string json = Json(await member.View(PluginId, "/", null, default));

        json.Should().NotContain("hand-written-wipe");
    }

    private static readonly IReadOnlyList<PluginNavEntry> HiddenOwnerNav =
    [
        new()
        {
            Section = PluginKind.Dashboard,
            Label = "hidden",
            Route = "/hidden",
            Access = PluginRouteAccess.Owner,
        },
    ];

    [Fact]
    public async Task Page_member_is_refused_an_owner_nav_route_the_plugin_never_declared()
    {
        Mock<IUiPlugin> plugin = Plugin(nav: HiddenOwnerNav);
        PluginUiController member = Controller(plugin.Object, User(MemberId));

        IActionResult result = await member.View(PluginId, "/hidden", null, default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        plugin.Verify(
            one => one.GetViewAsync(It.IsAny<PluginViewRequest>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Page_owner_opens_an_owner_nav_route_the_plugin_never_declared()
    {
        PluginUiController owner = Controller(Plugin(nav: HiddenOwnerNav).Object, User(OwnerId));

        IActionResult result = await owner.View(PluginId, "/hidden", null, default);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Page_member_still_opens_a_route_no_owner_nav_entry_names()
    {
        PluginUiController member = Controller(Plugin(nav: HiddenOwnerNav).Object, User(MemberId));

        IActionResult result = await member.View(PluginId, "/other", null, default);

        result.Should().BeOfType<OkObjectResult>();
    }

    // --- Hub (decision 6) ----------------------------------------------------

    private static (PluginHub Hub, PluginHubRouter Router, List<string> Ran) Hub(
        ClaimsPrincipal user
    )
    {
        Mock<IPluginManager> manager = new();
        manager.Setup(one => one.GetPluginInfo(PluginId)).Returns(Info(ws: true));
        PluginHubRouter router = new(manager.Object, NullLogger<PluginHubRouter>.Instance);

        List<string> ran = [];
        PluginDelegateHubHandler handler = router.DelegateHandlerFor(PluginId);
        handler.Register(
            "wipe",
            PluginRouteAccess.Owner,
            (_, _, _) =>
            {
                ran.Add("wipe");
                return Task.FromResult<object?>(null);
            }
        );
        handler.Register(
            "open",
            (_, _, _) =>
            {
                ran.Add("open");
                return Task.FromResult<object?>(null);
            }
        );

        Mock<HubCallerContext> context = new();
        context.SetupGet(one => one.User).Returns(user);
        context.SetupGet(one => one.ConnectionId).Returns("connection-1");
        context
            .SetupGet(one => one.UserIdentifier)
            .Returns(user.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        context.SetupGet(one => one.Features).Returns(new FeatureCollection());
        Mock<IHubCallerClients> clients = new();
        clients.SetupGet(one => one.Caller).Returns(new Mock<ISingleClientProxy>().Object);

        PluginHub hub = new(
            new Mock<IHttpContextAccessor>().Object,
            new Mock<IDbContextFactory<MediaContext>>().Object,
            new ConnectedClients(),
            new Mock<IActivityLogger>().Object,
            router,
            Resolver()
        )
        {
            Context = context.Object,
            Clients = clients.Object,
        };

        return (hub, router, ran);
    }

    [Fact]
    public async Task Hub_member_is_refused_a_method_registered_as_owner_only()
    {
        (PluginHub hub, _, List<string> ran) = Hub(User(MemberId));

        bool routed = await hub.Send(PluginId.ToString(), "wipe", JsonNode.Parse("{}"));

        routed.Should().BeFalse();
        ran.Should().BeEmpty();
    }

    [Fact]
    public async Task Hub_owner_reaches_a_method_registered_as_owner_only()
    {
        (PluginHub hub, _, List<string> ran) = Hub(User(OwnerId));

        bool routed = await hub.Send(PluginId.ToString(), "wipe", JsonNode.Parse("{}"));

        routed.Should().BeTrue();
        ran.Should().Equal("wipe");
    }

    [Fact]
    public async Task Hub_member_reaches_a_method_registered_without_an_access()
    {
        (PluginHub hub, _, List<string> ran) = Hub(User(MemberId));

        bool routed = await hub.Send(PluginId.ToString(), "open", JsonNode.Parse("{}"));

        routed.Should().BeTrue();
        ran.Should().Equal("open");
    }

    [Fact]
    public async Task Hub_a_role_claim_alone_does_not_make_a_member_the_owner()
    {
        (PluginHub hub, _, List<string> ran) = Hub(User(MemberId, role: "owner"));

        bool routed = await hub.Send(PluginId.ToString(), "wipe", JsonNode.Parse("{}"));

        routed.Should().BeFalse();
        ran.Should().BeEmpty();
    }

    [Fact]
    public async Task Hub_context_registers_an_owner_only_method_a_member_cannot_call()
    {
        (PluginHub hub, PluginHubRouter router, List<string> ran) = Hub(User(MemberId));
        IPluginHubContext context = new PluginHubBroadcaster(
            new Mock<IHubContext<PluginHub>>().Object,
            PluginId,
            router
        );
        context.HandleWithAccess(
            "secret",
            PluginRouteAccess.Owner,
            (_, _, _) =>
            {
                ran.Add("secret");
                return Task.FromResult<object?>(null);
            }
        );

        bool routed = await hub.Send(PluginId.ToString(), "secret", JsonNode.Parse("{}"));

        routed.Should().BeFalse();
        ran.Should().BeEmpty();
    }

    [Fact]
    public async Task Router_refuses_an_owner_only_method_for_a_member_caller()
    {
        (_, PluginHubRouter router, List<string> ran) = Hub(User(MemberId));

        bool routed = await router.RouteAsync(
            PluginId,
            new()
            {
                Method = "wipe",
                ConnectionId = "connection-1",
                Caller = MemberCaller(),
            },
            new Mock<IPluginHubClient>().Object,
            default
        );

        routed.Should().BeFalse();
        ran.Should().BeEmpty();
    }

    [Fact]
    public async Task Router_refuses_an_owner_only_method_when_the_message_has_no_caller()
    {
        (_, PluginHubRouter router, List<string> ran) = Hub(User(MemberId));
        Mock<IPluginHubHandler> plain = new();
        plain.SetupGet(one => one.PluginId).Returns(PluginId);
        plain
            .Setup(one =>
                one.HandleAsync(
                    It.IsAny<PluginHubMessage>(),
                    It.IsAny<IPluginHubClient>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(() => ran.Add("plain"))
            .Returns(Task.CompletedTask);
        router.Register(plain.Object);

        bool routed = await router.RouteAsync(
            PluginId,
            new() { Method = "wipe", ConnectionId = "connection-1" },
            new Mock<IPluginHubClient>().Object,
            default
        );

        routed.Should().BeFalse();
        ran.Should().BeEmpty();
    }
}
