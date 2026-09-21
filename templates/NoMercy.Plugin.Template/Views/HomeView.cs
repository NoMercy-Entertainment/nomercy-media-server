using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.Template.Views;

/// <summary>
/// The home page, described rather than drawn.
/// <para>
/// The host renders this on every client, so the page works with a remote
/// control without the plugin knowing what a remote control is. Shipping
/// markup instead would mean shipping it again for each surface.
/// </para>
/// </summary>
public static class HomeView
{
    public static PluginView Build() =>
        PluginViews.Declarative(
            PluginViews.Text("heading", "template.home.heading", "title"),
            PluginViews.Text("body", "template.home.body")
        );
}
