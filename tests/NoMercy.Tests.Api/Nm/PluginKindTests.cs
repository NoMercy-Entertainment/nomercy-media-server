using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.NmComponents;

public class PluginKindTests
{
    private static readonly Ulid Id = Ulid.NewUlid();

    [Fact]
    public void PutsAPluginWhereItBelongs()
    {
        // The point of the kind: a music plugin lands with the music, without
        // ever naming a path of its own.
        Assert.Equal($"/music/plugins/{Id}", PluginRoutes.PrefixFor(PluginKind.Music, Id));
        Assert.Equal($"/video/plugins/{Id}", PluginRoutes.PrefixFor(PluginKind.Video, Id));
        Assert.Equal($"/dashboard/plugins/{Id}", PluginRoutes.PrefixFor(PluginKind.Dashboard, Id));
    }

    [Fact]
    public void GivesAnAddonItsOwnPlaceRatherThanBuryingIt()
    {
        // An addon stands beside the app's own sections, so it does not read as
        // a sub-page of something it has nothing to do with.
        Assert.Equal($"/addons/{Id}", PluginRoutes.PrefixFor(PluginKind.Addon, Id));
        Assert.True(PluginKind.IsTopLevel(PluginKind.Addon));
        Assert.False(PluginKind.IsTopLevel(PluginKind.Music));
    }

    [Fact]
    public void GivesEveryClientOnePatternToRegister()
    {
        // A television app cannot add routes while running. One wildcard per
        // kind, registered when the app is built, is what lets it show a plugin
        // nobody had heard of at the time.
        Assert.Equal("/music/plugins/:pluginId/:route*", PluginRoutes.PatternFor(PluginKind.Music));
        Assert.Equal("/addons/:pluginId/:route*", PluginRoutes.PatternFor(PluginKind.Addon));
    }

    [Fact]
    public void RefusesToPlaceAKindNothingRecognises()
    {
        // Returning a plausible path for a kind no client routes would hide the
        // mistake until a viewer found a page that goes nowhere.
        Assert.Throws<ArgumentException>(() => PluginRoutes.PrefixFor("elsewhere", Id));
        Assert.Throws<ArgumentException>(() => PluginRoutes.PatternFor("elsewhere"));
    }

    [Fact]
    public void SaysBackendByShowingNoScreensRatherThanNamingIt()
    {
        // There is no backend kind. Backend is the absence of a mount, and
        // saying it twice would let a manifest contradict itself.
        Assert.DoesNotContain("backend", PluginKind.All);

        PluginCapabilities headless = new();
        Assert.Empty(headless.Ui?.Mounts ?? []);
    }

    [Fact]
    public void LetsOnePluginLandInMoreThanOnePlace()
    {
        // A subtitle plugin belongs with video and with the library. One kind
        // for the whole plugin would force a choice with no right answer.
        PluginCapabilities capabilities = new()
        {
            Ui = new()
            {
                Mounts =
                [
                    new()
                    {
                        Section = "Video",
                        Label = "subtitles",
                        Route = "/",
                        Kind = PluginKind.Video,
                    },
                    new()
                    {
                        Section = "Library",
                        Label = "subtitles.settings",
                        Route = "/settings",
                        Kind = PluginKind.Dashboard,
                    },
                ],
            },
        };

        Assert.Equal(
            [PluginKind.Video, PluginKind.Dashboard],
            capabilities.Ui!.Mounts.Select(mount => mount.Kind)
        );
    }

    [Fact]
    public void PromotesNothingToTheMainNavigationOnItsOwn()
    {
        // A request, not a setting. If every plugin could take a top-level slot
        // the navigation becomes a junk drawer and the last one installed wins
        // the most prominent place.
        PluginUiMount mount = new()
        {
            Section = "Library",
            Label = "x",
            Route = "/",
        };

        Assert.False(mount.RequestsTopLevel);
    }

    [Fact]
    public void KeepsEveryPlaceableKindDistinct()
    {
        // Two kinds sharing a prefix would put one plugin's pages inside
        // another's, and whichever loaded second would win.
        List<string> prefixes =
        [
            .. PluginKind
                .All.Where(PluginKind.DrawsUi)
                .Select(kind => PluginRoutes.PrefixFor(kind, Id)),
        ];

        Assert.Equal(prefixes.Count, prefixes.Distinct().Count());
    }

    [Fact]
    public void DefaultsToWhereEveryPluginUsedToLive()
    {
        // A manifest written before kinds existed keeps working, and keeps
        // appearing exactly where its author last saw it.
        PluginUiMount mount = new()
        {
            Section = "Advanced",
            Label = "x",
            Route = "/",
        };

        Assert.Equal(PluginKind.Dashboard, mount.Kind);
    }
}
