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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Events.Playback;
using NoMercy.Events.Plugins;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Hub;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginAbstractionsTests
{
    private sealed class TestPlugin : IPlugin
    {
        public string Name => "Test Plugin";
        public string Description => "A test plugin for unit testing";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0, 0);
        public bool Initialized { get; private set; }
        public IPluginContext? ReceivedContext { get; private set; }

        public void Initialize(IPluginContext context)
        {
            ReceivedContext = context;
            Initialized = true;
        }

        public void Dispose() { }
    }

    private sealed class TestMetadataPlugin : IMetadataPlugin
    {
        public string Name => "Test Metadata";
        public string Description => "Test metadata provider";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public Task<MediaMetadata?> GetMetadataAsync(
            string title,
            MediaType type,
            CancellationToken ct = default
        )
        {
            return Task.FromResult<MediaMetadata?>(
                new()
                {
                    Title = title,
                    Year = 2024,
                    Genres = ["Action"],
                }
            );
        }

        public void Dispose() { }
    }

    private sealed class TestMediaSourcePlugin : IMediaSourcePlugin
    {
        public string Name => "Test Source";
        public string Description => "Test media source";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public Task<IEnumerable<MediaFile>> ScanAsync(string path, CancellationToken ct = default)
        {
            return Task.FromResult<IEnumerable<MediaFile>>([
                new()
                {
                    Path = $"{path}/movie.mkv",
                    FileName = "movie.mkv",
                    Size = 1024,
                    Type = MediaType.Movie,
                },
            ]);
        }

        public void Dispose() { }
    }

    private sealed class TestEncoderPlugin : IEncoderPlugin
    {
        public string Name => "Test Encoder";
        public string Description => "Test encoder";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public EncodingProfile GetProfile(MediaInfo info)
        {
            return new()
            {
                Name = "test-profile",
                VideoCodec = "libx264",
                AudioCodec = "aac",
            };
        }

        public void Dispose() { }
    }

    private sealed class TestScheduledPlugin : IScheduledTaskPlugin
    {
        public string Name => "Test Scheduled";
        public string Description => "Test scheduled task";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0, 0);
        public string CronExpression => "0 0 * * *";
        public bool Executed { get; private set; }

        public void Initialize(IPluginContext context) { }

        public Task ExecuteAsync(CancellationToken ct = default)
        {
            Executed = true;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class TestAuthPlugin : IAuthPlugin
    {
        public string Name => "Test Auth";
        public string Description => "Test auth";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public Task<AuthResult> AuthenticateAsync(string token, CancellationToken ct = default)
        {
            return Task.FromResult(
                new AuthResult
                {
                    IsAuthenticated = token == "valid-token",
                    UserName = token == "valid-token" ? "testuser" : null,
                }
            );
        }

        public void Dispose() { }
    }

    private sealed class TestPluginContext : IPluginContext
    {
        private readonly IEventBus _eventBus;

        public IPluginEvents Events { get; }
        public ILogger Logger { get; }
        public string DataFolderPath { get; }
        public IPluginConfiguration Configuration { get; }
        public HttpClient HttpClient { get; }
        public Ulid PluginId { get; }
        public IPluginSecretStore Secrets { get; }
        public IPluginLibraryQuery Library { get; }
        public IPluginLibraryWriter? LibraryWriter => null;
        public IPluginGrants Grants { get; }
        public IPluginHubContext Hub { get; } = new NullPluginHubContext();

        public TestPluginContext(IEventBus eventBus, string dataFolder = "/tmp/plugin-test")
        {
            _eventBus = eventBus;
            Events = new PluginEvents(Ulid.Empty, eventBus);
            Logger = NullLogger.Instance;
            DataFolderPath = dataFolder;
            Configuration = new NullPluginConfiguration();
            HttpClient = new(new HttpClientHandler());
            PluginId = Ulid.Empty;
            Secrets = new InMemorySecretStore();
            Library = new NullPluginLibraryQuery();
            Grants = new DenyingGrants();
        }

        public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default) =>
            _eventBus.PublishAsync(PluginMessageEvent.From(PluginId, name, payload), ct);

        private sealed class DenyingGrants : IPluginGrants
        {
            public Task<bool> HasAsync(string kind, string value, CancellationToken ct = default) =>
                Task.FromResult(false);

            public Task<IReadOnlyList<string>> GetAsync(
                string kind,
                CancellationToken ct = default
            ) => Task.FromResult<IReadOnlyList<string>>([]);

            public Task RequestAsync(
                string kind,
                string value,
                string reason,
                CancellationToken ct = default
            ) => Task.CompletedTask;
        }

        private sealed class InMemorySecretStore : IPluginSecretStore
        {
            private readonly Dictionary<string, string> _values = [];

            public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
                Task.FromResult(_values.GetValueOrDefault(key));

            public Task SetAsync(string key, string value, CancellationToken ct = default)
            {
                _values[key] = value;
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string key, CancellationToken ct = default)
            {
                _values.Remove(key);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<string>> KeysAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<string>>(_values.Keys.ToList());

            // One caller in this double, so the per-user slot is its own key
            // space rather than a second dictionary.
            public Task<string?> GetForUserAsync(string key, CancellationToken ct = default) =>
                GetAsync($"user:{key}", ct);

            public Task SetForUserAsync(
                string key,
                string value,
                CancellationToken ct = default
            ) => SetAsync($"user:{key}", value, ct);

            public Task DeleteForUserAsync(string key, CancellationToken ct = default) =>
                DeleteAsync($"user:{key}", ct);
        }

        private sealed class MinimalServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }

        private sealed class NullPluginConfiguration : IPluginConfiguration
        {
            public T? GetConfiguration<T>()
                where T : class, new() => null;

            public Task<T?> GetConfigurationAsync<T>(CancellationToken ct = default)
                where T : class, new() => Task.FromResult<T?>(null);

            public void SaveConfiguration<T>(T configuration)
                where T : class { }

            public Task SaveConfigurationAsync<T>(T configuration, CancellationToken ct = default)
                where T : class => Task.CompletedTask;

            public bool HasConfiguration() => false;

            public void DeleteConfiguration() { }
        }
    }

    [Fact]
    public void IPlugin_CanBeImplemented_WithRequiredProperties()
    {
        using TestPlugin plugin = new();

        plugin.Name.Should().Be("Test Plugin");
        plugin.Description.Should().NotBeNullOrEmpty();
        plugin.Id.Should().NotBe(Ulid.Empty);
        plugin.Version.Should().Be(new(1, 0, 0));
    }

    [Fact]
    public void IPlugin_Initialize_ReceivesPluginContext()
    {
        InMemoryEventBus bus = new();
        TestPluginContext context = new(bus);

        using TestPlugin plugin = new();
        plugin.Initialize(context);

        plugin.Initialized.Should().BeTrue();
        plugin.ReceivedContext.Should().BeSameAs(context);
    }

    [Fact]
    public void IPluginContext_ProvidesTheTopicEventFacade()
    {
        InMemoryEventBus bus = new();
        TestPluginContext context = new(bus);

        context.Events.Should().BeOfType<PluginEvents>();
        typeof(IPluginContext).GetProperty("EventBus").Should().BeNull();
    }

    [Fact]
    public void IPluginContext_ProvidesLogger()
    {
        InMemoryEventBus bus = new();
        TestPluginContext context = new(bus);

        context.Logger.Should().NotBeNull();
    }

    [Fact]
    public void IPluginContext_ProvidesDataFolderPath()
    {
        InMemoryEventBus bus = new();
        TestPluginContext context = new(bus, "/data/plugins/my-plugin");

        context.DataFolderPath.Should().Be("/data/plugins/my-plugin");
    }

    [Fact]
    public async Task IMetadataPlugin_GetMetadataAsync_ReturnsMetadata()
    {
        using TestMetadataPlugin plugin = new();

        MediaMetadata? result = await plugin.GetMetadataAsync("Test Movie", MediaType.Movie);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Test Movie");
        result.Year.Should().Be(2024);
        result.Genres.Should().Contain("Action");
    }

    [Fact]
    public async Task IMediaSourcePlugin_ScanAsync_ReturnsFiles()
    {
        using TestMediaSourcePlugin plugin = new();

        List<MediaFile> files = (await plugin.ScanAsync("/movies")).ToList();

        files.Should().ContainSingle();
        files[0].FileName.Should().Be("movie.mkv");
        files[0].Type.Should().Be(MediaType.Movie);
        files[0].Size.Should().Be(1024);
    }

    [Fact]
    public void IEncoderPlugin_GetProfile_ReturnsEncodingProfile()
    {
        using TestEncoderPlugin plugin = new();

        MediaInfo info = new() { FilePath = "/test.mkv" };
        EncodingProfile profile = plugin.GetProfile(info);

        profile.Name.Should().Be("test-profile");
        profile.VideoCodec.Should().Be("libx264");
        profile.AudioCodec.Should().Be("aac");
    }

    [Fact]
    public async Task IScheduledTaskPlugin_ExecuteAsync_RunsTask()
    {
        using TestScheduledPlugin plugin = new();

        plugin.CronExpression.Should().Be("0 0 * * *");
        plugin.Executed.Should().BeFalse();

        await plugin.ExecuteAsync();

        plugin.Executed.Should().BeTrue();
    }

    [Fact]
    public async Task IAuthPlugin_AuthenticateAsync_ValidToken_ReturnsAuthenticated()
    {
        using TestAuthPlugin plugin = new();

        AuthResult result = await plugin.AuthenticateAsync("valid-token");

        result.IsAuthenticated.Should().BeTrue();
        result.UserName.Should().Be("testuser");
    }

    [Fact]
    public async Task IAuthPlugin_AuthenticateAsync_InvalidToken_ReturnsNotAuthenticated()
    {
        using TestAuthPlugin plugin = new();

        AuthResult result = await plugin.AuthenticateAsync("bad-token");

        result.IsAuthenticated.Should().BeFalse();
        result.UserName.Should().BeNull();
    }

    [Fact]
    public async Task IEncoderPlugin_GetProfileAsync_DefaultImplementation_WrapsGetProfile()
    {
        // TestEncoderPlugin never overrides GetProfileAsync — calling it through
        // the interface must dispatch to IEncoderPlugin's default implementation,
        // which wraps the synchronous GetProfile in Task.FromResult.
        using TestEncoderPlugin plugin = new();
        IEncoderPlugin encoderPlugin = plugin;
        MediaInfo info = new() { FilePath = "/test.mkv" };

        EncodingProfile? profile = await encoderPlugin.GetProfileAsync(info);

        profile.Should().NotBeNull();
        profile!.Name.Should().Be("test-profile");
    }

    [Fact]
    public async Task IPluginManager_InstallPluginAsyncWithChecksum_DefaultImplementation_ForwardsToTwoArgOverload()
    {
        // MinimalPluginManager never overrides the 3-arg InstallPluginAsync — calling
        // it through the interface must dispatch to IPluginManager's default
        // implementation, which forwards to the 2-arg (no-checksum) overload.
        MinimalPluginManager manager = new();
        IPluginManager pluginManager = manager;

        await pluginManager.InstallPluginAsync("https://example.com/plugin.zip", "deadbeef");

        manager
            .ReceivedPackageUrls.Should()
            .ContainSingle()
            .Which.Should()
            .Be("https://example.com/plugin.zip");
    }

    private sealed class MinimalPluginManager : IPluginManager
    {
        public List<string> ReceivedPackageUrls { get; } = [];

        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [];

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default)
        {
            ReceivedPackageUrls.Add(packageUrl);
            return Task.CompletedTask;
        }

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => [];
    }

    [Fact]
    public void PluginInfo_HoldsPluginMetadata()
    {
        PluginInfo info = new()
        {
            Id = Ulid.NewUlid(),
            Name = "My Plugin",
            Description = "A useful plugin",
            Version = new(2, 1, 0),
            Status = PluginStatus.Active,
            Author = "Test Author",
        };

        info.Name.Should().Be("My Plugin");
        info.Status.Should().Be(PluginStatus.Active);
        info.Version.Should().Be(new(2, 1, 0));
        info.Author.Should().Be("Test Author");
    }

    [Fact]
    public void PluginStatus_HasAllExpectedValues()
    {
        Enum.GetValues<PluginStatus>().Should().HaveCount(4);
        Enum.GetValues<PluginStatus>().Should().Contain(PluginStatus.Active);
        Enum.GetValues<PluginStatus>().Should().Contain(PluginStatus.Disabled);
        Enum.GetValues<PluginStatus>().Should().Contain(PluginStatus.Malfunctioned);
        Enum.GetValues<PluginStatus>().Should().Contain(PluginStatus.Deleted);
    }

    [Fact]
    public void MediaType_HasAllExpectedValues()
    {
        Enum.GetValues<MediaType>().Should().HaveCount(5);
        Enum.GetValues<MediaType>().Should().Contain(MediaType.Movie);
        Enum.GetValues<MediaType>().Should().Contain(MediaType.TvShow);
        Enum.GetValues<MediaType>().Should().Contain(MediaType.Music);
        Enum.GetValues<MediaType>().Should().Contain(MediaType.Episode);
        Enum.GetValues<MediaType>().Should().Contain(MediaType.Season);
    }

    [Fact]
    public void EncodingProfile_HasDefaults()
    {
        EncodingProfile profile = new()
        {
            Name = "test",
            VideoCodec = "h264",
            AudioCodec = "aac",
        };

        profile.Container.Should().Be("mp4");
        profile.Width.Should().BeNull();
        profile.ExtraParameters.Should().BeEmpty();
    }

    [Fact]
    public void MediaMetadata_HasDefaults()
    {
        MediaMetadata metadata = new() { Title = "Test" };

        metadata.Overview.Should().BeNull();
        metadata.Year.Should().BeNull();
        metadata.Genres.Should().BeEmpty();
        metadata.Rating.Should().BeNull();
    }

    [Fact]
    public void AuthResult_HasDefaults()
    {
        AuthResult result = new() { IsAuthenticated = false };

        result.UserId.Should().BeNull();
        result.UserName.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
        result.Claims.Should().BeEmpty();
    }

    [Fact]
    public void MediaFile_HasDefaults()
    {
        MediaFile file = new() { Path = "/test.mkv", FileName = "test.mkv" };

        file.Size.Should().Be(0);
        file.Type.Should().Be(MediaType.Movie);
        file.Properties.Should().BeEmpty();
    }

    [Fact]
    public void MediaInfo_HasDefaults()
    {
        MediaInfo info = new() { FilePath = "/test.mkv" };

        info.VideoCodec.Should().BeNull();
        info.AudioCodec.Should().BeNull();
        info.Width.Should().BeNull();
        info.Height.Should().BeNull();
        info.IsHdr.Should().BeFalse();
    }

    [Fact]
    public async Task Plugin_CanSubscribeToEventsViaContext()
    {
        InMemoryEventBus bus = new();
        TestPluginContext context = new(bus);

        List<string> received = [];
        context.Events.Subscribe<string>(
            "radio.started",
            (payload, _) =>
            {
                received.Add(payload);
                return Task.CompletedTask;
            }
        );

        await context.Events.PublishAsync("radio.stopped", "kink");
        await context.Events.PublishAsync("radio.started", "kink");

        received.Should().ContainSingle().Which.Should().Be("kink");
    }
}
