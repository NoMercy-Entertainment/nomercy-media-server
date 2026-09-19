using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.NmComponents;

public class PluginPlayerTests
{
    [Fact]
    public void SeparatesDrivingPlaybackFromChoosingWhatPlays()
    {
        // The capability grant covers reaching the player at all. Choosing what
        // comes out of a viewer's speakers is finer than that and asks
        // separately, because it is a different amount of trust.
        Assert.NotEqual(
            PluginGrantKind.PlayerSource,
            PluginGrantKind.ForCapability(PluginCapability.Player)
        );

        Assert.Equal("capability.player", PluginGrantKind.ForCapability(PluginCapability.Player));
        Assert.Equal("player.source", PluginGrantKind.PlayerSource);
    }

    [Fact]
    public void OffersNoPlayerUntilAHostProvidesOne()
    {
        // A host that predates this keeps compiling, and a plugin asking for a
        // player it was never given finds null rather than a stub that silently
        // does nothing.
        Assert.Null(((IPluginContext)new BareContext()).Player);
        Assert.Null(typeof(IPluginContext).GetProperty("System"));
    }

    [Fact]
    public void CarriesEnoughForAClientToDrawWhatIsPlaying()
    {
        PluginPlaybackSource stream = new()
        {
            Url = "https://example.invalid/stream.mp3",
            Title = "Radio",
            IsLive = true,
            PluginId = Ulid.NewUlid(),
        };

        Assert.True(stream.IsLive);
        Assert.NotEqual(Ulid.Empty, stream.PluginId);
    }

    [Fact]
    public void AcceptsOnlyTheCommandsAViewerAlreadyHas()
    {
        // A plugin should not be able to drive the player in ways the viewer
        // cannot, which is what keeps the two in the same mental model.
        foreach (string command in PluginPlaybackCommand.All)
            Assert.True(PluginPlaybackCommand.IsKnown(command));

        Assert.False(PluginPlaybackCommand.IsKnown("eject"));
        Assert.False(PluginPlaybackCommand.IsKnown(null));
    }

    [Fact]
    public void ReadsWhereAudioIsGoingRatherThanChoosingIt()
    {
        // The device is on the state a plugin reads. There is no way to set it,
        // because choosing it would overrule the viewer, and it is what lets
        // casting keep working without a plugin knowing casting exists.
        Assert.Null(typeof(PluginPlaybackSource).GetProperty("Device"));
        Assert.NotNull(typeof(PluginPlaybackState).GetProperty("Device"));
    }

    private class BareContext : IPluginContext
    {
        public IPluginEvents Events => null!;
        public Microsoft.Extensions.Logging.ILogger Logger => null!;
        public string DataFolderPath => string.Empty;
        public IPluginConfiguration Configuration => null!;
        public HttpClient HttpClient => null!;
        public Ulid PluginId => Ulid.Empty;
        public IPluginSecretStore Secrets => null!;
        public IPluginLibraryQuery Library => null!;
        public IPluginLibraryWriter LibraryWriter => null!;
        public IPluginGrants Grants => null!;
        public IPluginHubContext Hub => null!;

        public Task PublishAsync<T>(string type, T payload, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
