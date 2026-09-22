using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Tests.Api.NmComponents;

/// <summary>The caller a view request carries when the test is not about who asked.</summary>
internal static class PluginTestCaller
{
    public static PluginCaller Any { get; } =
        new(UserId.Empty, "Stoney", PluginRole.Owner, PluginAccess.Owned, "en", PluginSurface.Web);
}
