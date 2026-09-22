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

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginRepositoryTests : IDisposable
{
    private readonly string _tempDir;

    public PluginRepositoryTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-repo-tests-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException) { }
    }

    private const string RetiredIndex =
        "https://raw.githubusercontent.com/NoMercy-Entertainment/nomercy-plugins/master/index.json?v=1";

    private const string MarketplaceIndex = "https://api.nomercy.tv/v1/marketplace/index.json";

    private string WriteRepositoriesFile(params PluginRepositoryInfo[] repositories)
    {
        string configDir = Path.Combine(_tempDir, "configurations");
        Directory.CreateDirectory(configDir);
        string path = Path.Combine(configDir, "repositories.json");
        File.WriteAllText(path, JsonSerializer.Serialize(repositories));

        return path;
    }

    [Fact]
    public async Task A_server_still_holding_the_retired_index_is_moved_to_the_marketplace()
    {
        string path = WriteRepositoriesFile(
            new PluginRepositoryInfo
            {
                Name = "NoMercy Plugins",
                Url = RetiredIndex,
                Trusted = true,
            }
        );

        PluginRepository repository = new(
            new(),
            NullLogger.Instance,
            _tempDir,
            TestStorageHelper.CreateStorage(_tempDir)
        );
        await repository.LoadRepositoriesFromDiskAsync();

        repository
            .GetRepositories()
            .Should()
            .ContainSingle()
            .Which.Url.Should()
            .Be(MarketplaceIndex);

        File.ReadAllText(path)
            .Should()
            .Contain(MarketplaceIndex)
            .And.NotContain("raw.githubusercontent");
    }

    [Fact]
    public async Task A_repository_the_owner_added_is_left_alone()
    {
        WriteRepositoriesFile(
            new PluginRepositoryInfo
            {
                Name = "Mine",
                Url = "https://example.test/index.json",
                Trusted = false,
            }
        );

        PluginRepository repository = new(
            new(),
            NullLogger.Instance,
            _tempDir,
            TestStorageHelper.CreateStorage(_tempDir)
        );
        await repository.LoadRepositoriesFromDiskAsync();

        repository
            .GetRepositories()
            .Should()
            .ContainSingle()
            .Which.Url.Should()
            .Be("https://example.test/index.json");
    }

    private static PluginRepositoryManifest CreateTestManifest(
        string name = "test-repo",
        int pluginCount = 2
    )
    {
        List<PluginRepositoryEntry> plugins = [];
        for (int i = 0; i < pluginCount; i++)
        {
            plugins.Add(
                new()
                {
                    Id = Ulid.NewUlid(),
                    Name = $"Plugin{i}",
                    Description = $"Test plugin {i}",
                    Author = "Test Author",
                    Versions =
                    [
                        new()
                        {
                            Version = "1.0.0",
                            DownloadUrl = $"https://example.com/plugin{i}-1.0.0.zip",
                            TargetAbi = "9.0.0",
                            Changelog = "Initial release",
                        },
                    ],
                }
            );
        }

        return new()
        {
            Name = name,
            Url = "https://example.com/repo",
            Plugins = plugins,
        };
    }

    private static HttpClient CreateMockHttpClient(PluginRepositoryManifest manifest)
    {
        string json = JsonSerializer.Serialize(manifest);
        MockHttpHandler handler = new(json, HttpStatusCode.OK);
        return new(handler);
    }

    private static HttpClient CreateFailingHttpClient()
    {
        MockHttpHandler handler = new("", HttpStatusCode.InternalServerError);
        return new(handler);
    }

    private PluginRepository MakeRepo(HttpClient? client = null)
    {
        IStorageDriver driver = TestStorageHelper.CreateBackend();
        IStorage storage = new LocalStorage(driver, new([_tempDir], driver));
        return new(client ?? new HttpClient(), NullLogger.Instance, _tempDir, storage);
    }

    [Fact]
    public void Constructor_NullHttpClient_ThrowsArgumentNullException()
    {
        IStorage storage = TestStorageHelper.CreateStorage(_tempDir);
        Action act = () => new PluginRepository(null!, NullLogger.Instance, _tempDir, storage);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        IStorage storage = TestStorageHelper.CreateStorage(_tempDir);
        Action act = () => new PluginRepository(new(), null!, _tempDir, storage);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullPath_ThrowsArgumentException()
    {
        IStorage storage = TestStorageHelper.CreateStorage(_tempDir);
        Action act = () => new PluginRepository(new(), NullLogger.Instance, null!, storage);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullStorage_ThrowsArgumentNullException()
    {
        Action act = () => new PluginRepository(new(), NullLogger.Instance, _tempDir, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_CreatesConfigurationsDirectory()
    {
        string configDir = Path.Combine(_tempDir, "configurations");

        _ = MakeRepo();

        Directory.Exists(configDir).Should().BeTrue();
    }

    [Fact]
    public void Constructor_ConfigurationsDirectoryAlreadyExists_DoesNotThrow()
    {
        string configDir = Path.Combine(_tempDir, "configurations");
        Directory.CreateDirectory(configDir);

        Action act = () => MakeRepo();

        act.Should().NotThrow();
        Directory.Exists(configDir).Should().BeTrue();
    }

    [Fact]
    public void GetRepositories_Empty_ReturnsEmptyList()
    {
        PluginRepository repo = MakeRepo();

        IReadOnlyList<PluginRepositoryInfo> repos = repo.GetRepositories();

        repos.Should().BeEmpty();
    }

    [Fact]
    public async Task AddRepositoryAsync_AddsRepository()
    {
        PluginRepositoryManifest manifest = CreateTestManifest();
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("test", "https://example.com/repo.json");

        IReadOnlyList<PluginRepositoryInfo> repos = repo.GetRepositories();
        repos.Should().ContainSingle();
        repos[0].Name.Should().Be("test");
        repos[0].Url.Should().Be("https://example.com/repo.json");
        repos[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ARepositoryStartsUntrusted()
    {
        PluginRepository repo = MakeRepo();
        await repo.AddRepositoryAsync("test", "https://example.com/repo.json");

        repo.GetRepositories().Single(info => info.Name == "test").Trusted.Should().BeFalse();
    }

    [Fact]
    public async Task SetRepositoryTrustAsync_ChangesTheFlagAndPersistsIt()
    {
        PluginRepository repo = MakeRepo();
        await repo.AddRepositoryAsync("test", "https://example.com/repo.json");

        await repo.SetRepositoryTrustAsync("test", trusted: true);

        PluginRepository reader = MakeRepo();
        await reader.LoadAsync();
        reader.GetRepositories().Single(info => info.Name == "test").Trusted.Should().BeTrue();
    }

    [Fact]
    public async Task SetRepositoryTrustAsync_UnknownName_ThrowsInvalidOperation()
    {
        PluginRepository repo = MakeRepo();

        Func<Task> act = () => repo.SetRepositoryTrustAsync("nothing-here", trusted: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Trust_Withdrawn_StopsTrustingWhatThatIndexLists()
    {
        PluginRepositoryManifest manifest = CreateTestManifest();
        PluginRepository repo = MakeRepo(CreateMockHttpClient(manifest));
        await repo.AddRepositoryAsync("test", "https://example.com/repo.json");
        await repo.SetRepositoryTrustAsync("test", trusted: true);
        await repo.RefreshAsync();

        Ulid listed = repo.GetAvailablePlugins()[0].Id;
        repo.IsFromTrustedRepository(listed).Should().BeTrue();

        await repo.SetRepositoryTrustAsync("test", trusted: false);
        await repo.RefreshAsync();

        repo.IsFromTrustedRepository(listed).Should().BeFalse();
    }

    [Fact]
    public async Task AddRepositoryAsync_DuplicateName_ThrowsInvalidOperation()
    {
        PluginRepositoryManifest manifest = CreateTestManifest();
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("test", "https://example.com/repo1.json");
        Func<Task> act = () => repo.AddRepositoryAsync("test", "https://example.com/repo2.json");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task AddRepositoryAsync_NullName_ThrowsArgumentException()
    {
        PluginRepository repo = MakeRepo();

        Func<Task> act = () => repo.AddRepositoryAsync(null!, "https://example.com");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddRepositoryAsync_NullUrl_ThrowsArgumentException()
    {
        PluginRepository repo = MakeRepo();

        Func<Task> act = () => repo.AddRepositoryAsync("test", null!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AddRepositoryAsync_PersistsToDisk()
    {
        PluginRepositoryManifest manifest = CreateTestManifest();
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("persisted", "https://example.com/repo.json");

        string repoFile = Path.Combine(_tempDir, "configurations", "repositories.json");
        File.Exists(repoFile).Should().BeTrue();

        string json = await File.ReadAllTextAsync(repoFile);
        json.Should().Contain("persisted");
    }

    [Fact]
    public async Task AddRepositoryAsync_FetchesPluginsImmediately()
    {
        PluginRepositoryManifest manifest = CreateTestManifest(pluginCount: 3);
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("test", "https://example.com/repo.json");

        IReadOnlyList<PluginRepositoryEntry> plugins = repo.GetAvailablePlugins();
        plugins.Should().HaveCount(3);
    }

    [Fact]
    public async Task RemoveRepositoryAsync_RemovesRepository()
    {
        PluginRepositoryManifest manifest = CreateTestManifest();
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("test", "https://example.com/repo.json");
        await repo.RemoveRepositoryAsync("test");

        repo.GetRepositories().Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveRepositoryAsync_NotFound_ThrowsInvalidOperation()
    {
        PluginRepository repo = MakeRepo();

        Func<Task> act = () => repo.RemoveRepositoryAsync("nonexistent");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task RefreshAsync_FetchesFromAllEnabledRepos()
    {
        PluginRepositoryManifest manifest = CreateTestManifest(pluginCount: 2);
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("repo1", "https://example.com/repo1.json");
        await repo.RefreshAsync();

        IReadOnlyList<PluginRepositoryEntry> plugins = repo.GetAvailablePlugins();
        plugins.Should().HaveCount(2);
    }

    [Fact]
    public async Task RefreshAsync_FailingRepo_DoesNotThrow()
    {
        HttpClient client = CreateFailingHttpClient();
        PluginRepository repo = MakeRepo(client);

        // Manually add a repo without fetching (simulate pre-existing config)
        string configDir = Path.Combine(_tempDir, "configurations");
        string repoConfig = JsonSerializer.Serialize(
            new List<PluginRepositoryInfo>
            {
                new()
                {
                    Name = "broken",
                    Url = "https://broken.example.com/repo.json",
                    Enabled = true,
                },
            }
        );
        await File.WriteAllTextAsync(Path.Combine(configDir, "repositories.json"), repoConfig);

        PluginRepository repo2 = MakeRepo(client);
        Func<Task> act = () => repo2.RefreshAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RefreshAsync_OneRepoFailsAnotherSucceeds_SkipsFailingRepoKeepsOthers()
    {
        // RefreshAsync_FailingRepo_DoesNotThrow above builds repo2 through the sync
        // constructor, which never loads _repositories from disk — its enabled-repo
        // loop is empty and the per-repo catch (RefreshAsync's own, not
        // RefreshRepositoryAsync's) is never actually entered. This test adds two
        // repos through the real AddRepositoryAsync path so RefreshAsync's loop has
        // two live entries, one of which fails per request, proving the failure is
        // isolated to that repo and the other repo's plugins still come through.
        PluginRepositoryManifest goodManifest = CreateTestManifest(
            name: "good-repo",
            pluginCount: 2
        );
        RoutingHttpHandler handler = new(
            okUrl: "https://good.example.com/repo.json",
            okJson: JsonSerializer.Serialize(goodManifest)
        );
        HttpClient client = new(handler);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("good", "https://good.example.com/repo.json");
        await repo.AddRepositoryAsync("broken", "https://broken.example.com/repo.json");

        Func<Task> act = () => repo.RefreshAsync();

        await act.Should().NotThrowAsync();
        IReadOnlyList<PluginRepositoryEntry> plugins = repo.GetAvailablePlugins();
        plugins.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddRepositoryAsync_ConfigurationsPathReplacedByAFile_SaveFailsSilently()
    {
        // SaveRepositoriesToDiskAsync's own catch(Exception): WriteAllTextAsync
        // auto-creates a missing parent directory, so simply deleting
        // "configurations" would just have it silently recreated. Replacing it
        // with a FILE of the same name makes Directory.CreateDirectory itself
        // throw IOException ("a file with the same name already exists"),
        // which is the only way to force this specific write to fail — proving
        // AddRepositoryAsync still completes (the repo entry stands in memory)
        // instead of propagating the disk failure to the caller.
        PluginRepository repo = MakeRepo(CreateFailingHttpClient());
        string configDir = Path.Combine(_tempDir, "configurations");
        Directory.Delete(configDir, recursive: true);
        File.WriteAllText(configDir, "blocking file");

        Func<Task> act = () => repo.AddRepositoryAsync("test", "https://example.com/repo.json");

        await act.Should().NotThrowAsync();
        repo.GetRepositories().Should().ContainSingle();
    }

    [Fact]
    public async Task AddRepositoryAsync_FetchFails_StillAddsRepository_ButNoPluginsAvailable()
    {
        HttpClient client = CreateFailingHttpClient();
        PluginRepository repo = MakeRepo(client);

        Func<Task> act = () => repo.AddRepositoryAsync("test", "https://example.com/repo.json");

        await act.Should().NotThrowAsync();
        repo.GetRepositories().Should().ContainSingle().Which.Name.Should().Be("test");
        repo.GetAvailablePlugins().Should().BeEmpty();
    }

    [Fact]
    public async Task FetchRepositoryPluginsAsync_ResponseBodyIsJsonNull_ReturnsEmptyList()
    {
        MockHttpHandler handler = new("null", HttpStatusCode.OK);
        HttpClient client = new(handler);
        PluginRepository repo = MakeRepo(client);

        List<PluginRepositoryEntry> plugins = await repo.FetchRepositoryPluginsAsync(
            "https://example.com/repo.json"
        );

        plugins.Should().BeEmpty();
    }

    /// <summary>
    /// A server that has never had a repository list gets the default index, so
    /// the catalogue is reachable on a fresh install without anyone pasting a
    /// URL. This used to assert empty, which was right before there was an index
    /// to point at.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NoRepositoriesFileOnDisk_SeedsTheDefaultWithoutThrowing()
    {
        IStorageDriver driver = TestStorageHelper.CreateBackend();
        IStorage storage = new LocalStorage(driver, new([_tempDir], driver));

        // An unhandled exception here fails the test — the assertions below are
        // reached only when CreateAsync completed without throwing.
        PluginRepository repo = await PluginRepository.CreateAsync(
            new(),
            NullLogger.Instance,
            _tempDir,
            storage
        );

        PluginRepositoryInfo seeded = repo.GetRepositories().Should().ContainSingle().Subject;
        seeded.Url.Should().StartWith("https://");
        seeded.Enabled.Should().BeTrue();
    }

    /// <summary>
    /// Seeded once, not every boot. Removing the default has to stay removed, and
    /// it only does if the seed is written to the same file Remove then edits.
    /// </summary>
    [Fact]
    public async Task CreateAsync_DefaultRemoved_StaysRemovedOnTheNextStart()
    {
        IStorageDriver driver = TestStorageHelper.CreateBackend();
        IStorage storage = new LocalStorage(driver, new([_tempDir], driver));

        PluginRepository first = await PluginRepository.CreateAsync(
            new(),
            NullLogger.Instance,
            _tempDir,
            storage
        );
        await first.RemoveRepositoryAsync(
            first.GetRepositories().Single().Name,
            CancellationToken.None
        );

        PluginRepository second = await PluginRepository.CreateAsync(
            new(),
            NullLogger.Instance,
            _tempDir,
            storage
        );

        second.GetRepositories().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_MalformedRepositoriesFileOnDisk_ReturnsEmptyWithoutThrowing()
    {
        string configDir = Path.Combine(_tempDir, "configurations");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            Path.Combine(configDir, "repositories.json"),
            "not valid json {{{{"
        );

        IStorageDriver driver = TestStorageHelper.CreateBackend();
        IStorage storage = new LocalStorage(driver, new([_tempDir], driver));

        PluginRepository repo = await PluginRepository.CreateAsync(
            new(),
            NullLogger.Instance,
            _tempDir,
            storage
        );

        repo.GetRepositories().Should().BeEmpty();
    }

    [Fact]
    public void GetAvailablePlugins_NoRefresh_ReturnsEmpty()
    {
        PluginRepository repo = MakeRepo();

        IReadOnlyList<PluginRepositoryEntry> plugins = repo.GetAvailablePlugins();

        plugins.Should().BeEmpty();
    }

    [Fact]
    public async Task FindPlugin_ExistingId_ReturnsEntry()
    {
        PluginRepositoryManifest manifest = CreateTestManifest(pluginCount: 1);
        Ulid pluginId = manifest.Plugins[0].Id;
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("test", "https://example.com/repo.json");

        PluginRepositoryEntry? found = repo.FindPlugin(pluginId);

        found.Should().NotBeNull();
        found!.Id.Should().Be(pluginId);
    }

    [Fact]
    public void FindPlugin_UnknownId_ReturnsNull()
    {
        PluginRepository repo = MakeRepo();

        PluginRepositoryEntry? found = repo.FindPlugin(Ulid.NewUlid());

        found.Should().BeNull();
    }

    [Fact]
    public async Task FindVersion_ExistingVersion_ReturnsEntry()
    {
        PluginRepositoryManifest manifest = CreateTestManifest(pluginCount: 1);
        Ulid pluginId = manifest.Plugins[0].Id;
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("test", "https://example.com/repo.json");

        PluginVersionEntry? found = repo.FindVersion(pluginId, "1.0.0");

        found.Should().NotBeNull();
        found!.Version.Should().Be("1.0.0");
        found.DownloadUrl.Should().Contain("1.0.0");
    }

    [Fact]
    public void FindVersion_UnknownPlugin_ReturnsNull()
    {
        PluginRepository repo = MakeRepo();

        PluginVersionEntry? found = repo.FindVersion(Ulid.NewUlid(), "1.0.0");

        found.Should().BeNull();
    }

    [Fact]
    public async Task FindVersion_UnknownVersion_ReturnsNull()
    {
        PluginRepositoryManifest manifest = CreateTestManifest(pluginCount: 1);
        Ulid pluginId = manifest.Plugins[0].Id;
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);

        await repo.AddRepositoryAsync("test", "https://example.com/repo.json");

        PluginVersionEntry? found = repo.FindVersion(pluginId, "99.0.0");

        found.Should().BeNull();
    }

    [Fact]
    public void FindVersion_NullVersion_ThrowsArgumentException()
    {
        PluginRepository repo = MakeRepo();

        Action act = () => repo.FindVersion(Ulid.NewUlid(), null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PluginRepositoryManifest_CanDeserialize()
    {
        string json = """
            {
                "name": "Official Plugins",
                "url": "https://plugins.nomercy.tv/manifest.json",
                "plugins": [
                    {
                        "id": "0J6HB7G4HM28T14D0J6HB7H40J",
                        "name": "Scrobbler",
                        "description": "Last.fm scrobbling",
                        "author": "NoMercy",
                        "versions": [
                            {
                                "version": "1.0.0",
                                "targetAbi": "9.0.0",
                                "downloadUrl": "https://plugins.nomercy.tv/scrobbler-1.0.0.zip",
                                "checksum": "abc123",
                                "changelog": "Initial release",
                                "timestamp": "2026-01-01T00:00:00Z"
                            }
                        ]
                    }
                ]
            }
            """;

        PluginRepositoryManifest? manifest = JsonSerializer.Deserialize<PluginRepositoryManifest>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        manifest.Should().NotBeNull();
        manifest!.Name.Should().Be("Official Plugins");
        manifest.Plugins.Should().ContainSingle();
        manifest.Plugins[0].Name.Should().Be("Scrobbler");
        manifest.Plugins[0].Versions.Should().ContainSingle();
        manifest.Plugins[0].Versions[0].Checksum.Should().Be("abc123");
        manifest.Plugins[0].Versions[0].Timestamp.Should().NotBeNull();
    }

    [Fact]
    public void PluginRepositoryEntry_MultipleVersions()
    {
        PluginRepositoryEntry entry = new()
        {
            Id = Ulid.NewUlid(),
            Name = "TestPlugin",
            Description = "Test",
            Versions =
            [
                new() { Version = "1.0.0", DownloadUrl = "https://example.com/v1.zip" },
                new()
                {
                    Version = "2.0.0",
                    DownloadUrl = "https://example.com/release-2.zip",
                    TargetAbi = "9.0.0",
                },
            ],
        };

        entry.Versions.Should().HaveCount(2);
        entry.Versions[1].TargetAbi.Should().Be("9.0.0");
    }

    [Fact]
    public async Task CreateAsync_LoadsPersistedRepositories()
    {
        string configDir = Path.Combine(_tempDir, "configurations");
        Directory.CreateDirectory(configDir);

        List<PluginRepositoryInfo> repos =
        [
            new()
            {
                Name = "persisted-repo",
                Url = "https://example.com/persisted.json",
                Enabled = true,
            },
        ];
        string json = JsonSerializer.Serialize(repos);
        File.WriteAllText(Path.Combine(configDir, "repositories.json"), json);

        IStorageDriver driver = TestStorageHelper.CreateBackend();
        IStorage storage = new LocalStorage(driver, new([_tempDir], driver));
        PluginRepository repo = await PluginRepository.CreateAsync(
            new(),
            NullLogger.Instance,
            _tempDir,
            storage
        );

        IReadOnlyList<PluginRepositoryInfo> loaded = repo.GetRepositories();
        loaded.Should().ContainSingle();
        loaded[0].Name.Should().Be("persisted-repo");
    }

    // ── Concurrent save ───────────────────────────────────────────────────────
    //
    // SaveRepositoriesToDiskAsync used to serialize the live _repositories
    // list without holding _lock — a concurrent AddRepositoryAsync mutating
    // that same list (each call locks only around its own Add, then saves
    // outside the lock) could race the serializer's enumeration and throw
    // "Collection was modified; enumeration operation may not execute."

    [Fact]
    public async Task AddRepositoryAsync_ConcurrentCalls_DoNotThrowFromRacingSave()
    {
        PluginRepositoryManifest manifest = CreateTestManifest(pluginCount: 0);
        HttpClient client = CreateMockHttpClient(manifest);
        PluginRepository repo = MakeRepo(client);
        const int concurrentAdds = 20;

        Func<Task> act = () =>
            Task.WhenAll(
                Enumerable
                    .Range(0, concurrentAdds)
                    .Select(i =>
                        repo.AddRepositoryAsync($"repo-{i}", $"https://example.com/repo-{i}.json")
                    )
            );

        await act.Should().NotThrowAsync();

        IReadOnlyList<PluginRepositoryInfo> repos = repo.GetRepositories();
        repos.Should().HaveCount(concurrentAdds);
    }

    private sealed class MockHttpHandler(string responseContent, HttpStatusCode statusCode)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            HttpResponseMessage response = new(statusCode)
            {
                Content = new StringContent(responseContent),
            };
            return Task.FromResult(response);
        }
    }

    // Returns okJson for the one URL matching okUrl and a 500 for every other
    // URL — lets a single HttpClient represent "one repo fetch succeeds, every
    // other repo fetch fails" within the same PluginRepository instance.
    private sealed class RoutingHttpHandler(string okUrl, string okJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            bool isOk = string.Equals(
                request.RequestUri?.ToString(),
                okUrl,
                StringComparison.Ordinal
            );

            HttpResponseMessage response = new(
                isOk ? HttpStatusCode.OK : HttpStatusCode.InternalServerError
            )
            {
                Content = new StringContent(isOk ? okJson : string.Empty),
            };
            return Task.FromResult(response);
        }
    }
}
