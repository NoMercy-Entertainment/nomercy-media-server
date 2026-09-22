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

using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A third-party plugin implements <see cref="IUiPlugin"/> against the
/// published package, so the shape it has to satisfy is exercised here from the
/// outside rather than assumed.
/// </summary>
[Trait("Category", "Unit")]
public class UiPluginContractTests
{
    private static readonly PluginCaller AnyCaller = new(
        UserId.Empty,
        "Stoney",
        PluginRole.Owner,
        PluginAccess.Owned,
        "en",
        PluginSurface.Web
    );

    private sealed class DownloaderPlugin : IUiPlugin
    {
        public static readonly Ulid KnownId = Ulid.Parse("01ECH000000000000000000000");

        public string Name => "Downloader";
        public string Description => "Manages transfers";
        public Ulid Id => KnownId;
        public Version Version { get; } = new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public IReadOnlyList<PluginNavEntry> NavEntries =>
            [
                new()
                {
                    Section = PluginUiSection.Addon,
                    Label = "Downloads",
                    Route = "/",
                },
            ];

        public Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct) =>
            Task.FromResult(
                PluginViews.Declarative(
                    refreshInterval: 2,
                    PluginViews.Table(
                        "downloads",
                        [new() { Key = "name", Label = "Name" }],
                        [
                            PluginViews.Row(
                                "t1",
                                new Dictionary<string, object?> { ["name"] = request.Route }
                            ),
                        ]
                    )
                )
            );
    }

    [Fact]
    public async Task AUiPlugin_AnswersWithNavigationAndAViewPerRoute()
    {
        IUiPlugin plugin = new DownloaderPlugin();

        plugin.NavEntries[0].Label.Should().Be("Downloads");
        plugin.NavEntries[0].Section.Should().Be(PluginUiSection.Addon);

        PluginView view = await plugin.GetViewAsync(
            new() { Route = "/active", Caller = AnyCaller },
            CancellationToken.None
        );

        view.Components.Should().ContainSingle();
        // Items[0] is the header row now. The value is a text leaf in the body
        // row's first cell, because a cell holds a component rather than a
        // string keyed by column.
        view.Components![0].Items[1].Items[0].Items[0].Props["text"].Should().Be("/active");
    }

    [Fact]
    public async Task AViewThatChangesOnItsOwnSaysHowOftenToRefetch()
    {
        // Otherwise every client picks its own poll interval and the TV hammers
        // the server harder than the browser for the same screen.
        IUiPlugin plugin = new DownloaderPlugin();

        PluginView view = await plugin.GetViewAsync(
            new() { Route = "/", Caller = AnyCaller },
            CancellationToken.None
        );

        view.RefreshInterval.Should().Be(2);
    }

    [Fact]
    public void AUiPluginIsStillAnOrdinaryPlugin()
    {
        // IUiPlugin extends IPlugin rather than replacing it, so the loader,
        // the lifecycle and the teardown path all keep working unchanged.
        new DownloaderPlugin()
            .Should()
            .BeAssignableTo<IPlugin>();
    }
}
