using NoMercy.Plugin.Samples.Dashboard;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.NmComponents;

/// <summary>
/// The reference plugin, exercised the way a client exercises it.
///
/// It is the thing a plugin author copies, so it has to keep working. A change
/// that breaks the surface contract or the translation keys breaks the example
/// everyone learns from before it breaks anything a user sees.
/// </summary>
public class DashboardSamplePluginTests
{
    private static readonly DashboardSamplePlugin Plugin = new();

    private static async Task<PluginView> ViewFor(string surface)
    {
        return await Plugin.GetViewAsync(
            new() { Route = "/", Surface = surface, Caller = PluginTestCaller.Any },
            CancellationToken.None
        );
    }

    private static List<PluginComponent> Flatten(PluginComponent component)
    {
        List<PluginComponent> all = [component];
        foreach (PluginComponent child in component.Items)
            all.AddRange(Flatten(child));
        return all;
    }

    private static async Task<List<PluginComponent>> AllComponents(string surface)
    {
        PluginView view = await ViewFor(surface);
        return [.. (view.Components ?? []).SelectMany(Flatten)];
    }

    [Fact]
    public async Task ServesEverySurface()
    {
        foreach (string surface in PluginSurface.All)
        {
            PluginView view = await ViewFor(surface);
            Assert.NotEmpty(view.Components ?? []);
        }
    }

    [Fact]
    public async Task LaysOutForTheScreenItIsAskedFor()
    {
        // A phone gets one column and a television four. Serving the desktop
        // count everywhere is the failure this branch exists to avoid.
        Assert.Equal(1, await ColumnsOn(PluginSurface.Mobile));
        Assert.Equal(3, await ColumnsOn(PluginSurface.Web));
        Assert.Equal(4, await ColumnsOn(PluginSurface.Tv));
    }

    private static async Task<int> ColumnsOn(string surface)
    {
        PluginComponent card = (await AllComponents(surface)).First(component =>
            component.Id == "recent"
        );
        Dictionary<string, object?> box = (Dictionary<string, object?>)card.Props["box"]!;
        return (int)box["columns"]!;
    }

    [Fact]
    public async Task DropsTheDetailTableWhereThereIsNoRoomForIt()
    {
        Assert.Contains(
            await AllComponents(PluginSurface.Web),
            component => component.Id == "details"
        );
        Assert.DoesNotContain(
            await AllComponents(PluginSurface.Mobile),
            component => component.Id == "details"
        );
    }

    [Fact]
    public async Task NamesOnlyComponentsTheDesignSystemPublishes()
    {
        // A name the client cannot resolve renders nothing at all, and the
        // example would teach that shape to everyone who copies it.
        string[] known = ["NMCard", "NMContentHeader", "NMButton", "NMTable"];

        foreach (PluginComponent component in await AllComponents(PluginSurface.Web))
            Assert.Contains(component.Component, known);
    }

    [Fact]
    public async Task PutsNoEnglishOnTheWire()
    {
        // Every visible string is a key. A literal here would be text no viewer
        // could ever see translated, which is the whole point of the bundle.
        string[] keys = ["title", "empty", "play"];

        foreach (PluginComponent component in await AllComponents(PluginSurface.Web))
        {
            foreach (string field in new[] { "titleText", "ariaLabel", "text" })
            {
                if (
                    !component.Props.TryGetValue(field, out object? value)
                    || value is not string text
                )
                    continue;
                Assert.Contains(text, keys);
            }
        }
    }

    [Fact]
    public void NamesItsNavEntryByKeyToo()
    {
        Assert.Equal("title", Plugin.NavEntries[0].Label);
    }
}
