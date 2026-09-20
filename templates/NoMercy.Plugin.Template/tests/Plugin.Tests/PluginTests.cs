using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Testing;
using Xunit;

namespace NoMercy.Plugin.Template.Tests;

public class PluginTests
{
    [Fact]
    public void Initializing_does_not_throw()
    {
        FakePluginContext context = new("NoMercy.Plugin.Template 1.0.0");
        Plugin plugin = new();

        Action initialize = () => plugin.Initialize(context);

        initialize.Should().NotThrow();
    }

    [Fact]
    public void The_home_page_names_keys_rather_than_sentences()
    {
        PluginView view = Views.HomeView.Build();

        string snapshot = PluginViewSnapshot.Of(view);

        // Keys, not sentences. A sentence in the view is one no translator
        // can reach, and it arrives in English on a client whose owner never
        // chose English.
        snapshot.Should().Contain("template.home.heading");
        snapshot.Should().Contain("template.home.body");
    }
}
