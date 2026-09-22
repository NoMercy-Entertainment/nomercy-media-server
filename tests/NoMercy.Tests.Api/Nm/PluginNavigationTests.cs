using NoMercy.Plugin.Samples.Dashboard;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.NmComponents;

public class PluginNavigationTests
{
    private static readonly Ulid Id = Ulid.NewUlid();
    private static readonly DashboardSamplePlugin Plugin = new();

    [Fact]
    public void ResolvesAPluginRouteAgainstWhereverThePluginLives()
    {
        // The plugin wrote "/details/42" and nothing else. The same tree works
        // under music and under addons because it never wrote either down.
        PluginActionIntent intent = PluginNavigation.To("/details/42");

        Assert.Equal($"/music/plugins/{Id}/details/42", PluginNavigation.Resolve(intent, PluginKind.Music, Id));
        Assert.Equal($"/addons/{Id}/details/42", PluginNavigation.Resolve(intent, PluginKind.Addon, Id));
    }

    [Fact]
    public void SendsAPluginBackToItsOwnRoot()
    {
        Assert.Equal(
            $"/music/plugins/{Id}",
            PluginNavigation.Resolve(PluginNavigation.To("/"), PluginKind.Music, Id));
    }

    [Fact]
    public void LeavesAnAppPathAlone()
    {
        // Navigating out of its own space is the exception, and the only case
        // where a plugin is allowed to name a path the app owns.
        Assert.Equal(
            "/libraries/music",
            PluginNavigation.Resolve(PluginNavigation.ToApp("/libraries/music"), PluginKind.Music, Id));
    }

    [Fact]
    public void RefusesARouteThatIsNotOne()
    {
        // "details/42" would resolve against the prefix without a separator and
        // silently point at a sibling of the plugin.
        Assert.Throws<ArgumentException>(() => PluginNavigation.To("details/42"));
        Assert.Throws<ArgumentException>(() => PluginNavigation.ToApp("libraries"));
    }

    [Fact]
    public async Task ServesANestedPageOfItsOwn()
    {
        PluginView view = await Plugin.GetViewAsync(
            new() { Route = "/details/42", Surface = PluginSurface.Web, Caller = PluginTestCaller.Any }, CancellationToken.None);

        Assert.Contains(view.Components ?? [], component => component.Id == "detail");
    }

    [Fact]
    public async Task FallsBackToItsRootForARouteItDoesNotKnow()
    {
        // A viewer following a stale link should land on the plugin rather than
        // on an empty page that looks like the plugin is broken.
        PluginView view = await Plugin.GetViewAsync(
            new() { Route = "/gone", Surface = PluginSurface.Web, Caller = PluginTestCaller.Any }, CancellationToken.None);

        Assert.Contains(view.Components ?? [], component => component.Id == "recent");
    }

    [Fact]
    public void OffersAFormOnlyWhereItCanBeFilledIn()
    {
        PluginNavEntry settings = Plugin.NavEntries.First(entry => entry.Route == "/settings");

        Assert.True(settings.AppearsOn(PluginSurface.Web));
        Assert.False(settings.AppearsOn(PluginSurface.Tv));
    }

    [Fact]
    public void OffersAnEntryEverywhereWhenItNamesNoSurfaces()
    {
        // Saying nothing means everywhere. The opposite default would hide a
        // screen its author never thought about.
        PluginNavEntry browse = Plugin.NavEntries.First(entry => entry.Route == "/");

        foreach (string surface in PluginSurface.All)
            Assert.True(browse.AppearsOn(surface));
    }

    [Fact]
    public void PlacesItsTwoScreensInDifferentAreas()
    {
        // The reason kind is on the mount: one plugin, a browse page with the
        // music and its settings in the dashboard.
        Assert.Equal(PluginKind.Music, Plugin.NavEntries.First(entry => entry.Route == "/").Section);
        Assert.Equal(PluginKind.Dashboard, Plugin.NavEntries.First(entry => entry.Route == "/settings").Section);
    }
}

public class PluginSurfaceViewsTests
{
    private static PluginView Named(string id)
    {
        return new() { Components = [new() { Id = id, Component = "NMCard" }] };
    }

    private static string IdOf(PluginView view)
    {
        return view.Components![0].Id;
    }

    [Fact]
    public void GivesEachPlatformTheResponseItWasWrittenFor()
    {
        PluginSurfaceViews views = new()
        {
            Fallback = Named("fallback"),
            Mobile = Named("phone"),
            Tv = Named("television")
        };

        Assert.Equal("phone", IdOf(views.For(PluginSurface.Mobile)));
        Assert.Equal("television", IdOf(views.For(PluginSurface.Tv)));
    }

    [Fact]
    public void FallsBackForAPlatformNobodyAdapted()
    {
        // A blank page reads as a broken plugin rather than as one that was
        // never adapted, so there is no case where nothing renders.
        PluginSurfaceViews views = new() { Fallback = Named("fallback"), Tv = Named("television") };

        Assert.Equal("fallback", IdOf(views.For(PluginSurface.Web)));
        Assert.Equal("fallback", IdOf(views.For("something-nobody-serves")));
    }

    [Fact]
    public void SaysWhichPlatformsWereActuallyAdapted()
    {
        PluginSurfaceViews views = new() { Fallback = Named("fallback"), Tv = Named("television") };

        Assert.Equal([PluginSurface.Tv], views.Adapted());
    }

    [Fact]
    public async Task LetsTheSampleAnswerEachScreenOnItsOwnTerms()
    {
        DashboardSamplePlugin plugin = new();

        PluginView phone = await plugin.GetViewAsync(
            new() { Route = "/", Surface = PluginSurface.Mobile, Caller = PluginTestCaller.Any }, CancellationToken.None);
        PluginView television = await plugin.GetViewAsync(
            new() { Route = "/", Surface = PluginSurface.Tv, Caller = PluginTestCaller.Any }, CancellationToken.None);

        Assert.NotEqual(Columns(phone), Columns(television));
    }

    private static int Columns(PluginView view)
    {
        Dictionary<string, object?> box = (Dictionary<string, object?>)view.Components![0].Props["box"]!;
        return (int)box["columns"]!;
    }
}
