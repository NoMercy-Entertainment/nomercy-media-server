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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Api.Plugins;
using NoMercy.Events;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Hooks;
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Verification;
using NoMercy.Service.Hosting;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Service.Hosting;

/// <summary>
/// <see cref="PluginLoader"/> is the boot step that scans the plugins directory
/// and registers every plugin's cron jobs. A missing plugins directory must
/// return an empty list rather than throw (documented on <see cref="IPluginLoader.LoadPlugins"/>),
/// and cron registration must always run — even when zero plugins loaded — so a
/// plugin that only contributes a cron job without needing to "load" anything
/// still gets wired up.
/// </summary>
[Trait("Category", "Unit")]
public class PluginLoaderTests
{
    private static PluginLoadResult FakeResult(string name, string version) =>
        new(Ulid.NewUlid(), name, version, Mock.Of<IPlugin>());

    private static PluginApplicationPartRegistrar PartRegistrar() =>
        new(new(), new(), NullLogger<PluginApplicationPartRegistrar>.Instance);

    private static readonly Uri RevocationsAddress = new("https://example.test/revocations.json");

    /// <summary>The start-up refresh over a fake client that records each address asked.</summary>
    private static PluginRevocationStartupRefresh Refresh(List<string>? steps = null)
    {
        HttpClient http = new(new RecordingHandler(steps ?? []));

        return new(
            new(
                http,
                new(new Dictionary<string, string>()),
                NullLogger<PluginMarketplaceKeyClient>.Instance
            ),
            new(
                http,
                Mock.Of<IPluginRevocationStore>(),
                Mock.Of<IPluginTrustedKeys>(),
                NullLogger<PluginRevocationClient>.Instance
            ),
            new(
                new("https://example.test/keys.json"),
                RevocationsAddress,
                () => new("https://example.test/entitlements")
            ),
            NullLogger<PluginRevocationStartupRefresh>.Instance
        );
    }

    private sealed class RecordingHandler(List<string> steps) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            steps.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task LoadPlugins_AsksForTheRevocationListBeforeTheLoad()
    {
        List<string> steps = [];
        Mock<IPluginManager> pluginManager = new();
        pluginManager
            .Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("load"))
            .ReturnsAsync([]);

        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            Mock.Of<IPluginCronRegistrar>(),
            PartRegistrar(),
            Mock.Of<IPluginRepository>(),
            Refresh(steps),
            Mock.Of<IPluginRevocationEnforcer>()
        );

        await loader.LoadPlugins(CancellationToken.None);

        steps.Should().Contain(RevocationsAddress.ToString());
        steps
            .IndexOf(RevocationsAddress.ToString())
            .Should()
            .BeLessThan(steps.IndexOf("load"), "the load reads the list asked for at start-up");
    }

    [Fact]
    public async Task LoadPlugins_AListSavedDuringTheLoad_LeavesTheNamedPluginDisabled()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-load-recheck-" + Ulid.NewUlid());
        Directory.CreateDirectory(folder);
        Ulid revoked = Ulid.Parse("01HZX5D3K8QV7W2N4M6P9R0T1A");
        InMemoryEventBus events = new();
        NoMercy.PluginSdk.PluginRegistry registry = new();
        PluginRevocationStore store = new(Path.Combine(folder, "config"), events);
        NoMercy.PluginSdk.PluginRunGate gate = new([
            new NoMercy.PluginSdk.PluginRevocationRunCheck(
                new(store, TimeProvider.System, new(false, NullLogger.Instance)),
                store
            ),
        ]);
        Mock<IServiceProvider> services = new();
        services.Setup(s => s.GetService(typeof(NoMercy.PluginSdk.IPluginRunGate))).Returns(gate);
        LocalStorageDriver driver = new();
        using NoMercy.PluginSdk.PluginManager real = new(
            events,
            services.Object,
            NullLogger<NoMercy.PluginSdk.PluginManager>.Instance,
            folder,
            new LocalStorage(driver, new([folder], driver)),
            driver,
            registry: registry
        );
        Mock<IPlugin> instance = new();

        // The list arrives mid-load: its own scan runs before the plugin is up.
        Mock<IPluginManager> pluginManager = new();
        pluginManager
            .Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                store.Save(
                    new(
                        DateTimeOffset.UtcNow,
                        [new(revoked, null, null, "plugins.revoked.security")]
                    )
                );
                registry[revoked] = new(
                    new()
                    {
                        Id = revoked,
                        Name = "Sample",
                        Description = "d",
                        Version = new(0, 1, 0),
                        Status = PluginStatus.Active,
                        AssemblyPath = Path.Combine(folder, "Sample", "sample.dll"),
                        ManifestPath = Path.Combine(folder, "Sample", "plugin.json"),
                    },
                    instance.Object,
                    null
                );
            })
            .ReturnsAsync([]);

        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            Mock.Of<IPluginCronRegistrar>(),
            PartRegistrar(),
            Mock.Of<IPluginRepository>(),
            Refresh(),
            real
        );

        await loader.LoadPlugins(CancellationToken.None);

        registry.TryGetValue(revoked, out NoMercy.PluginSdk.LoadedPlugin? listed).Should().BeTrue();
        listed!
            .Info.Status.Should()
            .Be(PluginStatus.Disabled, "the list is read again after the load");
        listed.Instance.Should().BeNull();
        instance.Verify(plugin => plugin.Dispose(), Times.Once);
    }

    [Fact]
    public async Task LoadPlugins_ReturnsWhatPluginManagerLoads()
    {
        List<PluginLoadResult> expected = [FakeResult("Echo", "1.0.0")];
        Mock<IPluginManager> pluginManager = new();
        pluginManager
            .Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        Mock<IPluginCronRegistrar> cronRegistrar = new();

        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            cronRegistrar.Object,
            PartRegistrar(),
            Mock.Of<IPluginRepository>(),
            Refresh(),
            Mock.Of<IPluginRevocationEnforcer>()
        );

        IReadOnlyList<PluginLoadResult> result = await loader.LoadPlugins(CancellationToken.None);

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task LoadPlugins_NoPluginsFound_ReturnsEmptyListWithoutThrowing()
    {
        Mock<IPluginManager> pluginManager = new();
        pluginManager.Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Mock<IPluginCronRegistrar> cronRegistrar = new();

        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            cronRegistrar.Object,
            PartRegistrar(),
            Mock.Of<IPluginRepository>(),
            Refresh(),
            Mock.Of<IPluginRevocationEnforcer>()
        );

        IReadOnlyList<PluginLoadResult> result = await loader.LoadPlugins(CancellationToken.None);

        result.Should().BeEmpty();
        cronRegistrar.Verify(r => r.RegisterAll(), Times.Once);
    }

    [Fact]
    public async Task LoadPlugins_AlwaysRegistersCronJobsAfterLoading()
    {
        Mock<IPluginManager> pluginManager = new();
        pluginManager
            .Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([FakeResult("Sample", "2.0.0")]);
        Mock<IPluginCronRegistrar> cronRegistrar = new();

        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            cronRegistrar.Object,
            PartRegistrar(),
            Mock.Of<IPluginRepository>(),
            Refresh(),
            Mock.Of<IPluginRevocationEnforcer>()
        );
        await loader.LoadPlugins(CancellationToken.None);

        cronRegistrar.Verify(r => r.RegisterAll(), Times.Once);
    }

    [Fact]
    public async Task LoadPlugins_PassesCancellationTokenThrough()
    {
        Mock<IPluginManager> pluginManager = new();
        pluginManager.Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Mock<IPluginCronRegistrar> cronRegistrar = new();
        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            cronRegistrar.Object,
            PartRegistrar(),
            Mock.Of<IPluginRepository>(),
            Refresh(),
            Mock.Of<IPluginRevocationEnforcer>()
        );
        using CancellationTokenSource cts = new();

        await loader.LoadPlugins(cts.Token);

        pluginManager.Verify(m => m.LoadAllAsync(cts.Token), Times.Once);
    }

    /// <summary>
    /// The catalogue is refreshed at start, beside the boot rather than in it: a
    /// server that cannot reach the index must still boot, on the catalogue it
    /// cached the last time it could.
    /// </summary>
    [Fact]
    public async Task LoadPlugins_RefreshesTheCatalogueWithoutWaitingForIt()
    {
        Mock<IPluginManager> pluginManager = new();
        pluginManager.Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        TaskCompletionSource refreshStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource neverAnswers = new();
        Mock<IPluginRepository> repository = new();
        repository
            .Setup(r => r.RefreshAsync(It.IsAny<CancellationToken>()))
            .Callback(() => refreshStarted.TrySetResult())
            .Returns(neverAnswers.Task);
        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            Mock.Of<IPluginCronRegistrar>(),
            PartRegistrar(),
            repository.Object,
            Refresh(),
            Mock.Of<IPluginRevocationEnforcer>()
        );

        Task<IReadOnlyList<PluginLoadResult>> loading = loader.LoadPlugins(CancellationToken.None);

        (await Task.WhenAny(loading, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should()
            .BeSameAs(loading, "a refresh that never answers must not hold up the boot");
        (await Task.WhenAny(refreshStarted.Task, Task.Delay(TimeSpan.FromSeconds(10))))
            .Should()
            .BeSameAs(refreshStarted.Task, "the catalogue is refreshed at start");
    }

    [Fact]
    public async Task LoadPlugins_ARefreshThatThrows_DoesNotFailTheBoot()
    {
        Mock<IPluginManager> pluginManager = new();
        pluginManager.Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Mock<IPluginRepository> repository = new();
        repository
            .Setup(r => r.RefreshAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network unavailable"));
        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            Mock.Of<IPluginCronRegistrar>(),
            PartRegistrar(),
            repository.Object,
            Refresh(),
            Mock.Of<IPluginRevocationEnforcer>()
        );

        Func<Task> act = () => loader.LoadPlugins(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
