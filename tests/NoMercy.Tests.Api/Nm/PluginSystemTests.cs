using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.NmComponents;

public class PluginSystemTests
{
    [Fact]
    public void GatesEveryCapabilityWithoutAnyoneMaintainingASecondList()
    {
        // Derived rather than listed. A capability added tomorrow is gated by
        // the same rule, so nobody can add one and forget to protect it.
        foreach (string capability in PluginCapability.All)
            Assert.Equal($"capability.{capability}", PluginCapability.GrantFor(capability));
    }

    [Fact]
    public void SeparatesNotOfferedFromNotAllowed()
    {
        // A plugin should hide a feature the host cannot do and explain one it
        // is not permitted to do. Collapsing them makes both look like a bug.
        PluginSystemResult unsupported = PluginSystemResult.NotOffered(PluginCapability.Cast);
        PluginSystemResult refused = PluginSystemResult.NotAllowed(PluginCapability.Cast);

        Assert.True(unsupported.Unsupported);
        Assert.False(unsupported.Refused);

        Assert.True(refused.Refused);
        Assert.False(refused.Unsupported);

        Assert.False(unsupported.Ok);
        Assert.False(refused.Ok);
    }

    [Fact]
    public void RefusesWithoutThrowing()
    {
        // Being told no is an ordinary outcome a plugin handles, not an error
        // that takes the plugin down.
        PluginSystemResult refused = PluginSystemResult.NotAllowed(PluginCapability.Player);

        Assert.Contains("capability.player", refused.Reason);
    }

    [Fact]
    public void CoversMoreThanTheOneCapabilityThatPromptedIt()
    {
        // The player is one capability. A mechanism shaped around it would have
        // needed rewriting for the second.
        Assert.Contains(PluginCapability.Cast, PluginCapability.All);
        Assert.Contains(PluginCapability.Downloads, PluginCapability.All);
        Assert.Contains(PluginCapability.Notifications, PluginCapability.All);
        Assert.Contains(PluginCapability.Tasks, PluginCapability.All);
        Assert.Contains(PluginCapability.Library, PluginCapability.All);
    }

    [Fact]
    public void RejectsACapabilityNobodyOffers()
    {
        Assert.True(PluginCapability.IsKnown(PluginCapability.Player));
        Assert.False(PluginCapability.IsKnown("filesystem"));
        Assert.False(PluginCapability.IsKnown(null));
    }

    [Fact]
    public void KeepsTheTypedPlayerSpeakingTheSameWordsAsTheBus()
    {
        // A plugin using the convenience and one using InvokeAsync must send the
        // same command, or the host would need two vocabularies for one thing.
        Assert.Contains(PluginPlaybackCommand.Pause, PluginPlaybackCommand.All);
        Assert.Equal("pause", PluginPlaybackCommand.Pause);
    }
}
