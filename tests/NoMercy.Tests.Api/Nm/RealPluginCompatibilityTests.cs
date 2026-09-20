using System.Text.Json;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.NmComponents;

/// <summary>
/// The two plugins that actually exist, as their authors wrote them.
///
/// These manifests are copied verbatim from the published plugins rather than
/// written to suit the tests. Everything the routing and placement work adds has
/// to keep them working untouched: a plugin author does not rewrite their
/// manifest because the server grew a feature.
/// </summary>
public class RealPluginCompatibilityTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static PluginManifest Load(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Nm", "fixtures", name);
        return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), Json)!;
    }

    [Fact]
    public void ReadsBothManifestsAsTheirAuthorsWroteThem()
    {
        Assert.Equal("Internet Radio Provider", Load("radio.plugin.json").Name);
        Assert.Equal("Torrent Downloader", Load("torrent.plugin.json").Name);
    }

    [Fact]
    public void LeavesAPluginWithNoInterfaceAlone()
    {
        // The radio plugin is a media source with no UI at all. Nothing about
        // placement should apply to it, and it must not be asked where its
        // screens go.
        PluginManifest radio = Load("radio.plugin.json");

        Assert.Null(radio.Capabilities?.Ui);
    }

    [Fact]
    public void PlacesTheSectionAPublishedPluginAlreadyUses()
    {
        // The torrent plugin mounts under "settings". The spec follows what a
        // real plugin needed rather than the other way round, so settings is a
        // kind: where a plugin is set up, as opposed to where it does its work.
        PluginUiMount mount = Load("torrent.plugin.json").Capabilities!.Ui!.Mounts[0];

        Assert.Equal("settings", mount.Section);
        Assert.True(PluginKind.IsKnown(mount.Section));
        Assert.True(PluginKind.DrawsUi(mount.Section));
    }

    [Fact]
    public void GivesAMountThatNamesNoKindTheAreaItAlwaysHad()
    {
        // Written before kinds existed, so it says nothing. It has to keep
        // appearing exactly where its author last saw it.
        PluginUiMount mount = Load("torrent.plugin.json").Capabilities!.Ui!.Mounts[0];

        Assert.Equal(PluginKind.Dashboard, mount.Kind);
    }

    [Fact]
    public void OffersAMountEverywhereWhenItNamesNoSurfaces()
    {
        PluginUiMount mount = Load("torrent.plugin.json").Capabilities!.Ui!.Mounts[0];

        foreach (string surface in PluginSurface.All)
            Assert.True(mount.AppearsOn(surface));
    }

    [Fact]
    public void AsksNothingOfAPluginThatShipsNoTranslations()
    {
        // Neither plugin bundles any. The validator must stay silent rather than
        // reporting every string as untranslated.
        foreach (string name in new[] { "radio.plugin.json", "torrent.plugin.json" })
            Assert.Null(Load(name).Translations);
    }

    [Fact]
    public void PromotesNeitherOfThemToTheMainNavigation()
    {
        PluginUiMount mount = Load("torrent.plugin.json").Capabilities!.Ui!.Mounts[0];

        Assert.False(mount.RequestsTopLevel);
    }

    [Fact]
    public void ResolvesTheOneRouteTheTorrentPluginActuallyServes()
    {
        // Its whole interface is a single settings page. A route table has to
        // describe that as comfortably as it describes a dozen pages.
        PluginUiMount mount = Load("torrent.plugin.json").Capabilities!.Ui!.Mounts[0];

        PluginRouteTable table = new(
            new PluginRoute
            {
                Name = "settings",
                Path = mount.Route,
                Label = mount.Label,
                Layout = PluginLayout.Form,
            }
        );

        PluginRouteMatch? match = table.Resolve("/settings");

        Assert.NotNull(match);
        Assert.Equal("settings", match.Route.Name);
        Assert.Empty(match.Parameters);
    }
}
