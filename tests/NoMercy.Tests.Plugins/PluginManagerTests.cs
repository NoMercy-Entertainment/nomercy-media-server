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
using Moq;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginManagerTests : IDisposable
{
    private readonly string _tempPluginsDir;
    private readonly InMemoryEventBus _eventBus;
    private readonly PluginManager _manager;

    public PluginManagerTests()
    {
        _tempPluginsDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-plugin-tests-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempPluginsDir);

        _eventBus = new();
        IServiceProvider services = new MinimalServiceProvider();
        ILogger<PluginManager> logger = NullLogger<PluginManager>.Instance;

        _manager = new(
            _eventBus,
            services,
            logger,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            TestStorageHelper.CreateBackend()
        );
    }

    public void Dispose()
    {
        _manager.Dispose();

        try
        {
            if (Directory.Exists(_tempPluginsDir))
            {
                Directory.Delete(_tempPluginsDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public void Constructor_NullEventBus_Throws()
    {
        Action act = () =>
            new PluginManager(
                null!,
                new MinimalServiceProvider(),
                NullLogger<PluginManager>.Instance,
                "/tmp",
                TestStorageHelper.CreateStorage("/tmp"),
                TestStorageHelper.CreateBackend()
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("eventBus");
    }

    [Fact]
    public void Constructor_NullServiceProvider_Throws()
    {
        Action act = () =>
            new PluginManager(
                new InMemoryEventBus(),
                null!,
                NullLogger<PluginManager>.Instance,
                "/tmp",
                TestStorageHelper.CreateStorage("/tmp"),
                TestStorageHelper.CreateBackend()
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("serviceProvider");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Action act = () =>
            new PluginManager(
                new InMemoryEventBus(),
                new MinimalServiceProvider(),
                null!,
                "/tmp",
                TestStorageHelper.CreateStorage("/tmp"),
                TestStorageHelper.CreateBackend()
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_NullPluginsPath_Throws()
    {
        Action act = () =>
            new PluginManager(
                new InMemoryEventBus(),
                new MinimalServiceProvider(),
                NullLogger<PluginManager>.Instance,
                null!,
                TestStorageHelper.CreateStorage("/tmp"),
                TestStorageHelper.CreateBackend()
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("pluginsPath");
    }

    [Fact]
    public void Constructor_NullDriver_Throws()
    {
        Action act = () =>
            new PluginManager(
                new InMemoryEventBus(),
                new MinimalServiceProvider(),
                NullLogger<PluginManager>.Instance,
                "/tmp",
                TestStorageHelper.CreateStorage("/tmp"),
                null!
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("driver");
    }

    [Fact]
    public void Constructor_NullStorage_Throws()
    {
        Action act = () =>
            new PluginManager(
                new InMemoryEventBus(),
                new MinimalServiceProvider(),
                NullLogger<PluginManager>.Instance,
                "/tmp",
                null!,
                TestStorageHelper.CreateBackend()
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("storage");
    }

    [Fact]
    public void GetInstalledPlugins_NoPluginsLoaded_ReturnsEmptyList()
    {
        IReadOnlyList<PluginInfo> plugins = _manager.GetInstalledPlugins();

        plugins.Should().BeEmpty();
    }

    [Fact]
    public async Task InstallPluginAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        Func<Task> act = () => _manager.InstallPluginAsync("/nonexistent/path/plugin.dll");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task InstallPluginAsync_NullPath_ThrowsArgumentException()
    {
        Func<Task> act = () => _manager.InstallPluginAsync(null!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task InstallPluginAsync_EmptyPath_ThrowsArgumentException()
    {
        Func<Task> act = () => _manager.InstallPluginAsync("");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EnablePluginAsync_UnknownPluginId_ThrowsInvalidOperation()
    {
        Func<Task> act = () => _manager.EnablePluginAsync(Ulid.NewUlid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DisablePluginAsync_UnknownPluginId_ThrowsInvalidOperation()
    {
        Func<Task> act = () => _manager.DisablePluginAsync(Ulid.NewUlid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UninstallPluginAsync_UnknownPluginId_ThrowsInvalidOperation()
    {
        Func<Task> act = () => _manager.UninstallPluginAsync(Ulid.NewUlid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_EmptyDirectory_LoadsNothing()
    {
        await _manager.LoadPluginsFromDirectoryAsync();

        _manager.GetInstalledPlugins().Should().BeEmpty();
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_NonExistentDirectory_DoesNotThrow()
    {
        string nonExistentPath = Path.Combine(
            Path.GetTempPath(),
            "nonexistent-" + Ulid.NewUlid().ToString()
        );
        InMemoryEventBus bus = new();
        PluginManager manager = new(
            bus,
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            nonExistentPath,
            TestStorageHelper.CreateStorage(nonExistentPath),
            TestStorageHelper.CreateBackend()
        );

        Func<Task> act = () => manager.LoadPluginsFromDirectoryAsync();

        await act.Should().NotThrowAsync();
        manager.Dispose();
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_UnexpectedStorageFailureForOneDirectory_SkipsItAndContinues()
    {
        // "Defense in depth" (see the source comment on this catch): the per-plugin
        // load helpers already isolate their OWN failures internally, so the only
        // way to reach PluginManager's OWN catch here is a fault from the storage
        // layer itself — e.g. mid-scan I/O trouble on one specific directory while
        // the rest of the plugins root is fine. IStorage is PluginManager's
        // collaborator (not the type under test), so substituting a controlled
        // fault for it is the correct way to prove this isolation for real rather
        // than merely by reading the comment.
        Mock<IStorage> storage = new(MockBehavior.Strict);
        storage
            .Setup(s => s.CombinePath(_tempPluginsDir, "data", "platform"))
            .Returns(Path.Combine(_tempPluginsDir, "data", "platform"));
        storage
            .Setup(s => s.CombinePath(_tempPluginsDir, PluginManager.PendingUpdatesFolder))
            .Returns(Path.Combine(_tempPluginsDir, PluginManager.PendingUpdatesFolder));
        storage
            .Setup(s => s.CombinePath(_tempPluginsDir, PluginManager.RollbackFolder))
            .Returns(Path.Combine(_tempPluginsDir, PluginManager.RollbackFolder));
        storage
            .Setup(s => s.CombinePath(_tempPluginsDir, PluginManager.PendingDeletesFolder))
            .Returns(Path.Combine(_tempPluginsDir, PluginManager.PendingDeletesFolder));
        storage.Setup(s => s.Exists(_tempPluginsDir)).Returns(true);
        storage
            .Setup(s => s.List(_tempPluginsDir, null, false))
            .Returns([
                new StorageEntry("FaultyPlugin", true, 0, DateTimeOffset.UtcNow),
                new StorageEntry("GoodPlugin", true, 0, DateTimeOffset.UtcNow),
            ]);
        storage
            .Setup(s => s.CombinePath("FaultyPlugin", "plugin.json"))
            .Returns(Path.Combine("FaultyPlugin", "plugin.json"));
        storage.Setup(s => s.Exists(Path.Combine("FaultyPlugin", "plugin.json"))).Returns(false);
        storage
            .Setup(s => s.List("FaultyPlugin", "*.dll", false))
            .Throws(new IOException("simulated storage fault enumerating FaultyPlugin"));
        storage
            .Setup(s => s.CombinePath("GoodPlugin", "plugin.json"))
            .Returns(Path.Combine("GoodPlugin", "plugin.json"));
        storage.Setup(s => s.Exists(Path.Combine("GoodPlugin", "plugin.json"))).Returns(false);
        storage.Setup(s => s.List("GoodPlugin", "*.dll", false)).Returns([]);

        PluginManager faultyManager = new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _tempPluginsDir,
            storage.Object,
            TestStorageHelper.CreateBackend()
        );

        Func<Task> act = () => faultyManager.LoadPluginsFromDirectoryAsync();

        await act.Should()
            .NotThrowAsync("GoodPlugin must still be scanned after FaultyPlugin's failure");
        storage.Verify(s => s.List("GoodPlugin", "*.dll", false), Times.Once);
        faultyManager.Dispose();
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_StrayFileAtTopLevel_IsSkipped()
    {
        // The top-level listing is non-recursive and includes files alongside
        // plugin subdirectories — a stray file sitting directly in the plugins
        // root (never a valid plugin location) must be skipped via IsDirectory,
        // not treated as a plugin directory name.
        await File.WriteAllTextAsync(Path.Combine(_tempPluginsDir, "stray.txt"), "not a plugin");

        Func<Task> act = () => _manager.LoadPluginsFromDirectoryAsync();

        await act.Should().NotThrowAsync();
        _manager.GetInstalledPlugins().Should().BeEmpty();
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_SkipsConfigurationsAndDataDirs()
    {
        Directory.CreateDirectory(Path.Combine(_tempPluginsDir, "configurations"));
        Directory.CreateDirectory(Path.Combine(_tempPluginsDir, "data"));

        await _manager.LoadPluginsFromDirectoryAsync();

        _manager.GetInstalledPlugins().Should().BeEmpty();
    }

    [Fact]
    public async Task LoadPluginAssemblyAsync_InvalidDll_PublishesErrorEvent()
    {
        string pluginDir = Path.Combine(_tempPluginsDir, "BadPlugin");
        Directory.CreateDirectory(pluginDir);
        string dllPath = Path.Combine(pluginDir, "BadPlugin.dll");
        await File.WriteAllTextAsync(dllPath, "not a valid dll");

        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _manager.LoadPluginAssemblyAsync(dllPath);

        errors.Should().ContainSingle();
        errors[0].PluginName.Should().Be("BadPlugin");
        errors[0].ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoadPluginAssemblyAsync_InvalidDll_UnloadsContext()
    {
        string pluginDir = Path.Combine(_tempPluginsDir, "BadPlugin2");
        Directory.CreateDirectory(pluginDir);
        string dllPath = Path.Combine(pluginDir, "BadPlugin2.dll");
        await File.WriteAllTextAsync(dllPath, "garbage data");

        await _manager.LoadPluginAssemblyAsync(dllPath);

        _manager.GetInstalledPlugins().Should().BeEmpty();
    }

    [Fact]
    public void GetPluginInstance_UnknownId_ReturnsNull()
    {
        IPlugin? result = _manager.GetPluginInstance(Ulid.NewUlid());

        result.Should().BeNull();
    }

    [Fact]
    public void GetPluginsOfType_NoPlugins_ReturnsEmpty()
    {
        IEnumerable<IMetadataPlugin> result = _manager.GetPluginsOfType<IMetadataPlugin>();

        result.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        InMemoryEventBus bus = new();
        PluginManager manager = new(
            bus,
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            TestStorageHelper.CreateBackend()
        );

        Action act = () =>
        {
            manager.Dispose();
            manager.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void PluginLoadContext_IsCollectible()
    {
        string dummyPath = Path.Combine(_tempPluginsDir, "dummy.dll");
        File.WriteAllBytes(dummyPath, []);

        PluginLoadContext context = new(dummyPath);

        context.IsCollectible.Should().BeTrue();
    }

    [Fact]
    public void PluginContext_StoresAllProperties()
    {
        InMemoryEventBus bus = new();
        MinimalServiceProvider services = new();
        ILogger logger = NullLogger.Instance;
        string dataFolder = _tempPluginsDir;

        PluginContext context = TestPluginPlatform.Context(
            bus,
            dataFolder,
            TestStorageHelper.CreateStorage(dataFolder),
            services: services
        );

        context.EventBus.Should().BeSameAs(bus);
        context.Services.Should().BeSameAs(services);
        context.Logger.Should().BeSameAs(logger);
        context.DataFolderPath.Should().Be(dataFolder);
    }

    [Fact]
    public void PluginContext_NullEventBus_Throws()
    {
        Action act = () =>
            new PluginContext(
                Ulid.Empty,
                null!,
                new MinimalServiceProvider(),
                NullLogger.Instance,
                _tempPluginsDir,
                TestStorageHelper.CreateStorage(_tempPluginsDir),
                TestPluginPlatform.Secrets(),
                new NullPluginLibraryQuery(),
                TestPluginPlatform.Grants()
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("eventBus");
    }

    [Fact]
    public void PluginContext_NullServices_Throws()
    {
        Action act = () =>
            new PluginContext(
                Ulid.Empty,
                new InMemoryEventBus(),
                null!,
                NullLogger.Instance,
                _tempPluginsDir,
                TestStorageHelper.CreateStorage(_tempPluginsDir),
                TestPluginPlatform.Secrets(),
                new NullPluginLibraryQuery(),
                TestPluginPlatform.Grants()
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }

    [Fact]
    public void PluginContext_NullLogger_Throws()
    {
        Action act = () =>
            new PluginContext(
                Ulid.Empty,
                new InMemoryEventBus(),
                new MinimalServiceProvider(),
                null!,
                _tempPluginsDir,
                TestStorageHelper.CreateStorage(_tempPluginsDir),
                TestPluginPlatform.Secrets(),
                new NullPluginLibraryQuery(),
                TestPluginPlatform.Grants()
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void PluginContext_NullDataFolder_Throws()
    {
        Action act = () =>
            new PluginContext(
                Ulid.Empty,
                new InMemoryEventBus(),
                new MinimalServiceProvider(),
                NullLogger.Instance,
                null!,
                TestStorageHelper.CreateStorage(_tempPluginsDir),
                TestPluginPlatform.Secrets(),
                new NullPluginLibraryQuery(),
                TestPluginPlatform.Grants()
            );
        act.Should().Throw<ArgumentNullException>().WithParameterName("dataFolderPath");
    }

    [Fact]
    public void GetInstalledPlugins_ReturnsReadOnlyList()
    {
        IReadOnlyList<PluginInfo> plugins = _manager.GetInstalledPlugins();

        plugins.Should().BeAssignableTo<IReadOnlyList<PluginInfo>>();
    }

    [Fact]
    public async Task LoadPluginFromManifestAsync_MissingAssembly_PublishesErrorEvent()
    {
        Ulid pluginId = Ulid.NewUlid();
        string pluginDir = Path.Combine(_tempPluginsDir, "TestPlugin");
        Directory.CreateDirectory(pluginDir);

        string manifestJson =
            $@"{{
            ""id"": ""{pluginId}"",
            ""name"": ""TestPlugin"",
            ""description"": ""A test"",
            ""version"": ""1.0.0"",
            ""assembly"": ""NonExistent.dll""
        }}";
        string manifestPath = Path.Combine(pluginDir, "plugin.json");
        await File.WriteAllTextAsync(manifestPath, manifestJson);

        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _manager.LoadPluginFromManifestAsync(manifestPath);

        errors.Should().ContainSingle();
        errors[0].PluginName.Should().Be("TestPlugin");
        errors[0].ErrorMessage.Should().Contain("NonExistent.dll");
    }

    [Fact]
    public async Task LoadPluginFromManifestAsync_InvalidManifest_PublishesErrorEvent()
    {
        string pluginDir = Path.Combine(_tempPluginsDir, "BadManifest");
        Directory.CreateDirectory(pluginDir);

        string manifestPath = Path.Combine(pluginDir, "plugin.json");
        await File.WriteAllTextAsync(manifestPath, "not valid json");

        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _manager.LoadPluginFromManifestAsync(manifestPath);

        errors.Should().ContainSingle();
        errors[0].PluginName.Should().Be("BadManifest");
        errors[0].ErrorMessage.Should().Contain("Invalid plugin manifest");
    }

    [Fact]
    public async Task LoadPluginFromManifestAsync_InvalidDll_PublishesErrorEvent()
    {
        Ulid pluginId = Ulid.NewUlid();
        string pluginDir = Path.Combine(_tempPluginsDir, "BadDll");
        Directory.CreateDirectory(pluginDir);

        string dllPath = Path.Combine(pluginDir, "BadDll.dll");
        await File.WriteAllTextAsync(dllPath, "not a valid dll");

        string manifestJson =
            $@"{{
            ""id"": ""{pluginId}"",
            ""name"": ""BadDll"",
            ""description"": ""A test"",
            ""version"": ""1.0.0"",
            ""assembly"": ""BadDll.dll""
        }}";
        string manifestPath = Path.Combine(pluginDir, "plugin.json");
        await File.WriteAllTextAsync(manifestPath, manifestJson);

        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _manager.LoadPluginFromManifestAsync(manifestPath);

        errors.Should().ContainSingle();
        errors[0].PluginName.Should().Be("BadDll");
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_PrefersManifestOverDllScan()
    {
        Ulid pluginId = Ulid.NewUlid();
        string pluginDir = Path.Combine(_tempPluginsDir, "ManifestPlugin");
        Directory.CreateDirectory(pluginDir);

        string dllPath = Path.Combine(pluginDir, "ManifestPlugin.dll");
        await File.WriteAllTextAsync(dllPath, "garbage data");

        string manifestJson =
            $@"{{
            ""id"": ""{pluginId}"",
            ""name"": ""ManifestPlugin"",
            ""description"": ""Uses manifest"",
            ""version"": ""1.0.0"",
            ""assembly"": ""ManifestPlugin.dll""
        }}";
        string manifestPath = Path.Combine(pluginDir, "plugin.json");
        await File.WriteAllTextAsync(manifestPath, manifestJson);

        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _manager.LoadPluginsFromDirectoryAsync();

        errors.Should().ContainSingle();
        errors[0].PluginName.Should().Be("ManifestPlugin");
    }

    // ── Boot-time cleanup: nothing a hot update or uninstall leaves behind
    //    should ever survive past the next start. ──────────────────────────

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_QuarantinedDeleteWithNoLock_IsActuallyDeleted()
    {
        // What an uninstall leaves behind when its own delete attempt could
        // not complete: DeleteOrQueueForDeletionAsync's real output, not a
        // hand-built fixture, so this proves the two halves actually agree on
        // the folder name shape (<name>-<ulid>).
        Ulid id = Ulid.NewUlid();
        _manager.GetInstalledPlugins().Should().BeEmpty();

        string quarantined = Path.Combine(
            _tempPluginsDir,
            PluginManager.PendingDeletesFolder,
            $"OldPlugin-{Ulid.NewUlid()}"
        );
        Directory.CreateDirectory(quarantined);
        await File.WriteAllTextAsync(Path.Combine(quarantined, "OldPlugin.dll"), "gone");

        await _manager.LoadPluginsFromDirectoryAsync();

        Directory
            .Exists(quarantined)
            .Should()
            .BeFalse("the next start must finish the delete an uninstall could not");
        Directory
            .Exists(Path.Combine(_tempPluginsDir, PluginManager.PendingDeletesFolder))
            .Should()
            .BeFalse(
                "an empty pending-deletes folder reads as something still queued when nothing is"
            );
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_RollbackFolderWithNoInstalledCopy_IsRestored()
    {
        // The crash-before-cleanup case: the old folder was moved into
        // .rollback and the process died before the staged copy could be
        // swapped in, so the installed folder is simply missing.
        string rollbackDir = Path.Combine(_tempPluginsDir, PluginManager.RollbackFolder, "Radio");
        Directory.CreateDirectory(rollbackDir);
        await File.WriteAllTextAsync(Path.Combine(rollbackDir, "Radio.dll"), "the only copy left");

        await _manager.LoadPluginsFromDirectoryAsync();

        string restored = Path.Combine(_tempPluginsDir, "Radio");
        Directory
            .Exists(restored)
            .Should()
            .BeTrue("the rollback copy is the only one left, so it must come back");
        File.Exists(Path.Combine(restored, "Radio.dll")).Should().BeTrue();
        Directory.Exists(rollbackDir).Should().BeFalse("moved into place, not merely copied");
        Directory
            .Exists(Path.Combine(_tempPluginsDir, PluginManager.RollbackFolder))
            .Should()
            .BeFalse("an empty rollback folder must not linger once the last entry is resolved");
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_RollbackFolderWithInstalledCopyPresent_IsDiscarded()
    {
        // The crash-after-cleanup-would-have-run case: the swap completed and
        // the installed folder is there, so the rollback copy is stale.
        string installed = Path.Combine(_tempPluginsDir, "Radio");
        Directory.CreateDirectory(installed);
        await File.WriteAllTextAsync(
            Path.Combine(installed, "Radio.dll"),
            "the version that landed"
        );

        string rollbackDir = Path.Combine(_tempPluginsDir, PluginManager.RollbackFolder, "Radio");
        Directory.CreateDirectory(rollbackDir);
        await File.WriteAllTextAsync(Path.Combine(rollbackDir, "Radio.dll"), "the old version");

        await _manager.LoadPluginsFromDirectoryAsync();

        Directory
            .Exists(rollbackDir)
            .Should()
            .BeFalse("an update that completed leaves nothing to roll back to");
        File.ReadAllText(Path.Combine(installed, "Radio.dll"))
            .Should()
            .Be(
                "the version that landed",
                "discarding the rollback must never touch the installed copy"
            );
        Directory
            .Exists(Path.Combine(_tempPluginsDir, PluginManager.RollbackFolder))
            .Should()
            .BeFalse("an empty rollback folder must not linger once the last entry is resolved");
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_StaleAssemblyBackupWithOriginalPresent_IsDeleted()
    {
        // A single-dll update that completed but could not remove its own
        // ".rollback" backup file in the same instant.
        string pluginDir = Path.Combine(_tempPluginsDir, "Radio");
        Directory.CreateDirectory(pluginDir);
        string assemblyPath = Path.Combine(pluginDir, "Radio.dll");
        await File.WriteAllTextAsync(assemblyPath, "the version that landed");
        string backupPath = assemblyPath + PluginManager.RollbackSuffix;
        await File.WriteAllTextAsync(backupPath, "the old version");

        await _manager.LoadPluginsFromDirectoryAsync();

        File.Exists(backupPath)
            .Should()
            .BeFalse("a completed update leaves nothing to restore from");
        File.ReadAllText(assemblyPath).Should().Be("the version that landed");
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_StaleAssemblyBackupWithOriginalMissing_IsRestored()
    {
        // The crash-mid-swap case for a single-dll plugin: the original was
        // moved to its backup name and the process died before the new file
        // could be copied into place, so the assembly itself is gone.
        string pluginDir = Path.Combine(_tempPluginsDir, "Radio");
        Directory.CreateDirectory(pluginDir);
        string assemblyPath = Path.Combine(pluginDir, "Radio.dll");
        string backupPath = assemblyPath + PluginManager.RollbackSuffix;
        await File.WriteAllTextAsync(backupPath, "the only copy left");

        await _manager.LoadPluginsFromDirectoryAsync();

        File.Exists(backupPath).Should().BeFalse("moved into place, not merely copied");
        File.Exists(assemblyPath)
            .Should()
            .BeTrue("the backup is the only copy left, so it must come back");
        File.ReadAllText(assemblyPath).Should().Be("the only copy left");
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_TwoRollbackFolders_EachResolvedByItsOwnName()
    {
        // The distinction that would silently collapse if the resolver ever
        // confused one folder for another (a shared flag, a positional index
        // instead of a name lookup): two plugins in .rollback at once, one that
        // must come back and one that must be thrown away, resolved in the
        // SAME pass. A bug that applied one decision to both would flip one of
        // these and this would still show only one failure, not neither.
        string radioRollback = Path.Combine(_tempPluginsDir, PluginManager.RollbackFolder, "Radio");
        Directory.CreateDirectory(radioRollback);
        await File.WriteAllTextAsync(
            Path.Combine(radioRollback, "Radio.dll"),
            "the only Radio copy left"
        );

        string weatherInstalled = Path.Combine(_tempPluginsDir, "Weather");
        Directory.CreateDirectory(weatherInstalled);
        await File.WriteAllTextAsync(
            Path.Combine(weatherInstalled, "Weather.dll"),
            "the Weather version that landed"
        );
        string weatherRollback = Path.Combine(
            _tempPluginsDir,
            PluginManager.RollbackFolder,
            "Weather"
        );
        Directory.CreateDirectory(weatherRollback);
        await File.WriteAllTextAsync(
            Path.Combine(weatherRollback, "Weather.dll"),
            "the old Weather version"
        );

        await _manager.LoadPluginsFromDirectoryAsync();

        Directory
            .Exists(Path.Combine(_tempPluginsDir, "Radio"))
            .Should()
            .BeTrue("Radio had no installed copy, so its rollback must come back");
        Directory
            .Exists(weatherRollback)
            .Should()
            .BeFalse("Weather's update completed, so its rollback is stale");
        File.ReadAllText(Path.Combine(weatherInstalled, "Weather.dll"))
            .Should()
            .Be(
                "the Weather version that landed",
                "discarding Weather's rollback must never touch Weather's installed copy"
            );
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_TwoStaleAssemblyBackups_EachResolvedByItsOwnName()
    {
        string radioDir = Path.Combine(_tempPluginsDir, "Radio");
        Directory.CreateDirectory(radioDir);
        string radioAssembly = Path.Combine(radioDir, "Radio.dll");
        string radioBackup = radioAssembly + PluginManager.RollbackSuffix;
        await File.WriteAllTextAsync(radioBackup, "the only Radio copy left");

        string weatherDir = Path.Combine(_tempPluginsDir, "Weather");
        Directory.CreateDirectory(weatherDir);
        string weatherAssembly = Path.Combine(weatherDir, "Weather.dll");
        await File.WriteAllTextAsync(weatherAssembly, "the Weather version that landed");
        string weatherBackup = weatherAssembly + PluginManager.RollbackSuffix;
        await File.WriteAllTextAsync(weatherBackup, "the old Weather version");

        await _manager.LoadPluginsFromDirectoryAsync();

        File.Exists(radioAssembly)
            .Should()
            .BeTrue("the Radio backup is the only copy left, so it must come back");
        File.Exists(weatherBackup)
            .Should()
            .BeFalse("Weather's update completed, so its backup is stale");
        File.ReadAllText(weatherAssembly)
            .Should()
            .Be(
                "the Weather version that landed",
                "discarding Weather's backup must never touch Weather's assembly"
            );
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
