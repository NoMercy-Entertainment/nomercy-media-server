// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2024 NoMercy Entertainment

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Samples.Dashboard;

/// <summary>
/// The reference for a plugin with a screen.
///
/// It exists to be copied. Everything here is the shape a real plugin should
/// have: a tree of components rather than markup, a view that answers each
/// surface on its own terms, and text referenced by key so it can be translated.
/// </summary>
public class DashboardSamplePlugin : IUiPlugin
{
    public Ulid Id => Ulid.Parse("01DASHB0ARD000000000000000");

    public string Name => "Dashboard Sample";

    public string Description => "Reference plugin: a screen built from components";

    public Version Version => new(0, 1, 0);

    /// <summary>
    /// The pages this plugin serves, declared so the server can list them and a
    /// client can register a named route for each when a server is chosen.
    /// </summary>
    public static readonly PluginRouteTable Table = new(
        new PluginRoute
        {
            Name = "browse",
            Path = "/",
            Label = "title",
            Layout = PluginLayout.Grid
        },
        new PluginRoute
        {
            Name = "detail",
            Path = "/details/:id",
            Label = "title",
            Layout = PluginLayout.ListDetail,
            LayoutBySurface = { [PluginSurface.Tv] = PluginLayout.Immersive }
        },
        new PluginRoute
        {
            Name = "settings",
            Path = "/settings",
            Label = "settings",
            Layout = PluginLayout.Form,
            Surfaces = [PluginSurface.Web]
        });

    public PluginRouteTable Routes => Table;

    public IReadOnlyList<PluginNavEntry> NavEntries =>
    [
        new()
        {
            Section = PluginKind.Music,
            // A key, not a label. The client resolves it under this plugin's
            // namespace, so a Dutch viewer reads Dutch here too.
            Label = "title",
            Route = "/",
            Icon = "grid"
        },
        new()
        {
            Section = PluginKind.Dashboard,
            Label = "settings",
            Route = "/settings",
            Icon = "cog",
            // Offered nowhere but a desktop: it is a form, and a form behind a
            // remote control is a page nobody finishes.
            Surfaces = [PluginSurface.Web]
        }
    ];

    public void Initialize(IPluginContext context)
    {
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct)
    {
        // Branch only where the page is genuinely a different page. A television
        // at four metres is not a narrow desktop. Differences of a single hidden
        // element belong on that element's own box instead.
        // The plugin owns everything under its prefix. The route is its own
        // route space, so nesting is just a path it recognises, and pages it
        // does not know fall back to its own root rather than to nothing.
        return Task.FromResult(request.Route switch
        {
            "/settings" => SettingsScreen(),
            var route when route.StartsWith("/details/") => DetailScreen(route["/details/".Length..]),
            // Declared rather than branched. The platforms sit beside each
            // other, so one left unadapted is visible instead of falling
            // through to whichever branch happened to be last.
            _ => PluginViews.PerSurface(
                request.Surface,
                fallback: Screen(columns: 3, showDetails: true),
                mobile: Screen(columns: 1, showDetails: false),
                tv: Screen(columns: 4, showDetails: false)
            )
        });
    }

    /// <summary>
    /// One screen. Every string is a key the client resolves, never English
    /// written into the payload.
    /// </summary>
    private static PluginView Screen(int columns, bool showDetails)
    {
        PluginComponent card = new()
        {
            Id = "recent",
            Component = "NMCard",
            Props = new()
            {
                ["box"] = new Dictionary<string, object?>
                {
                    ["padding"] = new Dictionary<string, object?> { ["all"] = "4" },
                    ["columns"] = columns
                }
            },
            Items =
            [
                new()
                {
                    Id = "heading",
                    Component = "NMContentHeader",
                    Props = new() { ["titleText"] = "title" }
                },
                new()
                {
                    Id = "play",
                    Component = "NMButton",
                    Props = new() { ["ariaLabel"] = "play" },
                    // Relative to this plugin. It never writes its own prefix,
                    // which is what lets the same tree work wherever it is
                    // mounted and survive being moved to another kind.
                    Action = PluginNavigation.To("/details/42")
                }
            ]
        };

        if (showDetails)
            card.Items.Add(new()
            {
                Id = "details",
                Component = "NMTable",
                // Hidden rather than absent on the small surfaces, so one tree
                // serves all three where the difference is only what shows.
                Props = new()
                {
                    ["box"] = new Dictionary<string, object?>
                    {
                        ["hiddenOn"] = new[] { PluginSurface.Mobile, PluginSurface.Tv }
                    }
                }
            });

        return new() { Components = [card] };
    }

    /// <summary>A nested page, reached from the card above.</summary>
    private static PluginView DetailScreen(string id)
    {
        return new()
        {
            Components =
            [
                new()
                {
                    Id = "detail",
                    Component = "NMCard",
                    Items =
                    [
                        new()
                        {
                            Id = "detail-heading",
                            Component = "NMContentHeader",
                            Props = new() { ["titleText"] = "title", ["subtitleText"] = id }
                        },
                        new()
                        {
                            Id = "back",
                            Component = "NMButton",
                            Props = new() { ["ariaLabel"] = "back" },
                            Action = PluginNavigation.To("/")
                        }
                    ]
                }
            ]
        };
    }

    /// <summary>The settings page, which only a desktop is offered.</summary>
    private static PluginView SettingsScreen()
    {
        return new()
        {
            Components =
            [
                new()
                {
                    Id = "settings",
                    Component = "NMCard",
                    Items =
                    [
                        new()
                        {
                            Id = "settings-heading",
                            Component = "NMContentHeader",
                            Props = new() { ["titleText"] = "settings" }
                        }
                    ]
                }
            ]
        };
    }
}
