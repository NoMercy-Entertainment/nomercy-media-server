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

using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Unit-tests <see cref="PluginLifecycleManager"/> directly against a real
/// <see cref="PluginRegistry"/> and real <see cref="PluginLoader"/>, bypassing
/// <see cref="PluginManager"/> entirely. InternalsVisibleTo makes every type
/// here reachable; going through PluginManager's full assembly-loading pipeline
/// for every Enable/Disable/Uninstall branch would require staging a real
/// plugin assembly per scenario for no additional correctness value — the
/// LoadedPlugin objects this class operates on are the real production
/// contract, constructed directly instead of round-tripped through a loader.
/// </summary>
public class PluginLifecycleManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryEventBus _eventBus;
    private readonly PluginRegistry _registry;
    private readonly PluginLifecycleManager _lifecycle;

    public PluginLifecycleManagerTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-lifecycle-mgr-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempDir);

        _eventBus = new();
        _registry = new();
        PluginLoader loader = new(
            _eventBus,
            new MinimalServiceProvider(),
            NullLogger.Instance,
            _tempDir,
            TestStorageHelper.CreateStorage(_tempDir),
            _registry,
            new PluginVerifier(),
            new PluginConsentService(new InMemoryConsentStore()),
            TestPluginPlatform.ContextFactory(_eventBus, TestStorageHelper.CreateStorage(_tempDir))
        );

        _lifecycle = new(
            _eventBus,
            new MinimalServiceProvider(),
            NullLogger.Instance,
            _tempDir,
            TestStorageHelper.CreateStorage(_tempDir),
            _registry,
            loader,
            TestPluginPlatform.ContextFactory(_eventBus, TestStorageHelper.CreateStorage(_tempDir))
        );
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                // A read-only file that blocked a delete under test is quarantined
                // rather than left at its original path, but it is still read-only
                // wherever it landed — clear every attribute before the recursive
                // delete below, or that delete fails the same way the one under
                // test did.
                foreach (
                    string file in Directory.EnumerateFiles(
                        _tempDir,
                        "*",
                        SearchOption.AllDirectories
                    )
                )
                    File.SetAttributes(file, FileAttributes.Normal);

                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static PluginInfo Info(Ulid id, PluginStatus status, string? assemblyPath = null) =>
        new()
        {
            Id = id,
            Name = "Test Plugin",
            Description = "d",
            Version = new(1, 0, 0),
            Status = status,
            AssemblyPath = assemblyPath,
        };

    // ── EnablePluginAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task EnablePluginAsync_UnknownId_ThrowsInvalidOperation()
    {
        Func<Task> act = () => _lifecycle.EnablePluginAsync(Ulid.NewUlid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task EnablePluginAsync_AlreadyActive_IsANoOp()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active), plugin, null);

        await _lifecycle.EnablePluginAsync(id);

        plugin
            .InitializeCallCount.Should()
            .Be(0, "an already-active plugin must not be re-initialized");
    }

    [Fact]
    public async Task EnablePluginAsync_DisabledWithInstance_InitializesAndTransitionsToActive()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Disabled), plugin, null);
        List<PluginLoadedEvent> loaded = [];
        _eventBus.Subscribe<PluginLoadedEvent>(
            (evt, _) =>
            {
                loaded.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _lifecycle.EnablePluginAsync(id);

        plugin.InitializeCallCount.Should().Be(1);
        _registry.TryGetValue(id, out LoadedPlugin? afterward).Should().BeTrue();
        afterward!.Info.Status.Should().Be(PluginStatus.Active);
        loaded.Should().ContainSingle(e => e.PluginId == id.ToString());
    }

    [Fact]
    public async Task EnablePluginAsync_DisabledWithInstance_CreatesDataFolderWhenMissing()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Disabled), plugin, null);
        string dataFolder = Path.Combine(_tempDir, "data", id.ToString());

        Directory.Exists(dataFolder).Should().BeFalse();

        await _lifecycle.EnablePluginAsync(id);

        Directory.Exists(dataFolder).Should().BeTrue();
    }

    [Fact]
    public async Task EnablePluginAsync_NullInstanceWithAssemblyPath_DelegatesToLoaderAndReturns()
    {
        // No manifest/assembly actually needs to exist on disk for THIS
        // assertion — LoadPluginAssemblyAsync's own load-context-construction
        // catch reports the failure as a PluginErrorOccurredEvent rather than
        // throwing, so EnablePluginAsync completes either way. What this proves
        // is that the null-instance branch defers to the loader instead of
        // trying to call Initialize() on a null reference.
        Ulid id = Ulid.NewUlid();
        string assemblyPath = Path.Combine(_tempDir, "missing-plugin.dll");
        _registry[id] = new(Info(id, PluginStatus.Disabled, assemblyPath), null, null);

        Func<Task> act = () => _lifecycle.EnablePluginAsync(id);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnablePluginAsync_DeletedStatusWithInstance_RethrowsInvalidOperationWithoutMalfunctioning()
    {
        // Deleted is a terminal status — PluginLifecycle.Transition(Deleted, Active)
        // itself throws InvalidOperationException, and EnablePluginAsync's own
        // `catch (InvalidOperationException) { throw; }` must let that specific
        // exception through unmodified rather than recording it as a generic
        // malfunction.
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Deleted), plugin, null);

        Func<Task> act = () => _lifecycle.EnablePluginAsync(id);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _registry.TryGetValue(id, out LoadedPlugin? afterward).Should().BeTrue();
        afterward!
            .Info.Status.Should()
            .Be(
                PluginStatus.Deleted,
                "the failed transition must not be recorded as Malfunctioned"
            );
    }

    [Fact]
    public async Task EnablePluginAsync_InitializeThrows_MarksMalfunctionedAndPublishesErrorEvent()
    {
        Ulid id = Ulid.NewUlid();
        ThrowingInitializePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Disabled), plugin, null);
        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _lifecycle.EnablePluginAsync(id);

        _registry.TryGetValue(id, out LoadedPlugin? afterward).Should().BeTrue();
        afterward!.Info.Status.Should().Be(PluginStatus.Malfunctioned);
        errors.Should().ContainSingle(e => e.PluginId == id.ToString());
    }

    // ── DisablePluginAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task DisablePluginAsync_UnknownId_ThrowsInvalidOperation()
    {
        Func<Task> act = () => _lifecycle.DisablePluginAsync(Ulid.NewUlid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DisablePluginAsync_AlreadyDisabled_IsANoOp()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Disabled), plugin, null);

        await _lifecycle.DisablePluginAsync(id);

        plugin
            .DisposeCallCount.Should()
            .Be(0, "an already-disabled plugin's instance must not be disposed again");
    }

    [Fact]
    public async Task DisablePluginAsync_Active_DisposesInstanceAndTransitionsToDisabled()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active), plugin, null);

        await _lifecycle.DisablePluginAsync(id);

        plugin.DisposeCallCount.Should().Be(1);
        _registry.TryGetValue(id, out LoadedPlugin? afterward).Should().BeTrue();
        afterward!.Info.Status.Should().Be(PluginStatus.Disabled);
    }

    [Fact]
    public async Task DisableThenEnable_DoesNotReinitializeTheDisposedInstance()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        string assemblyPath = Path.Combine(_tempDir, "missing-plugin.dll");
        _registry[id] = new(Info(id, PluginStatus.Active, assemblyPath), plugin, null);

        await _lifecycle.DisablePluginAsync(id);

        _registry.TryGetValue(id, out LoadedPlugin? disabled).Should().BeTrue();
        disabled!
            .Instance.Should()
            .BeNull("a disposed instance must not stay in the registry as the live one");

        await _lifecycle.EnablePluginAsync(id);

        plugin
            .InitializeCallCount.Should()
            .Be(
                0,
                "enabling must build a fresh instance, not re-initialize the one that was disposed"
            );
    }

    [Fact]
    public async Task DisablePluginAsync_ActiveWithNullInstance_DoesNotThrow()
    {
        Ulid id = Ulid.NewUlid();
        _registry[id] = new(Info(id, PluginStatus.Active), null, null);

        Func<Task> act = () => _lifecycle.DisablePluginAsync(id);

        await act.Should().NotThrowAsync();
    }

    // ── RestartPluginAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task RestartPluginAsync_UnknownId_ThrowsInvalidOperation()
    {
        Func<Task> act = () => _lifecycle.RestartPluginAsync(Ulid.NewUlid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RestartPluginAsync_Active_DisablesBeforeAttemptingToEnableAgain()
    {
        // The orchestration this method exists for: Active goes through Disable
        // first rather than straight to Enable, which would take the
        // already-Active short-circuit and do nothing at all. Whether the
        // plugin actually comes back Active depends on there being a real,
        // loadable assembly behind AssemblyPath — proved separately by the
        // real-plugin round trip in PluginHotUpdateTests, not reproducible with
        // this fixture's fake instance and no assembly on disk.
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active), plugin, null);

        await _lifecycle.RestartPluginAsync(id);

        plugin.DisposeCallCount.Should().Be(1, "the plugin that was running must be disposed");
    }

    [Fact]
    public async Task RestartPluginAsync_Disabled_EnablesWithoutDisablingFirst()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Disabled), plugin, null);

        await _lifecycle.RestartPluginAsync(id);

        plugin
            .DisposeCallCount.Should()
            .Be(0, "a plugin that was already disabled has nothing to dispose again");
        plugin.InitializeCallCount.Should().Be(1);
        _registry.TryGetValue(id, out LoadedPlugin? afterward).Should().BeTrue();
        afterward!.Info.Status.Should().Be(PluginStatus.Active);
    }

    // ── UnloadForUpdateAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UnloadForUpdateAsync_ReleasesScheduledWork_WhilePluginStillInRegistry()
    {
        // Same failure mode as the uninstall case below, on the hot-update
        // path: PluginCronRegistrar reads the plugin's Jobs list back out of
        // the registry to remove its named cron executors by name, and finds
        // nothing to remove once the registry entry is already gone.
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active), plugin, null);
        bool wasStillRegisteredWhenReleased = false;

        PluginLifecycleManager lifecycle = new(
            _eventBus,
            new MinimalServiceProvider(),
            NullLogger.Instance,
            _tempDir,
            TestStorageHelper.CreateStorage(_tempDir),
            _registry,
            new PluginLoader(
                _eventBus,
                new MinimalServiceProvider(),
                NullLogger.Instance,
                _tempDir,
                TestStorageHelper.CreateStorage(_tempDir),
                _registry,
                new PluginVerifier(),
                new PluginConsentService(new InMemoryConsentStore()),
                TestPluginPlatform.ContextFactory(
                    _eventBus,
                    TestStorageHelper.CreateStorage(_tempDir)
                )
            ),
            TestPluginPlatform.ContextFactory(_eventBus, TestStorageHelper.CreateStorage(_tempDir)),
            releaseScheduledWork: releasedId =>
                wasStillRegisteredWhenReleased = _registry.TryGetValue(releasedId, out _)
        );

        bool result = await lifecycle.UnloadForUpdateAsync(id);

        result.Should().BeTrue();
        wasStillRegisteredWhenReleased.Should().BeTrue();
        _registry.TryGetValue(id, out _).Should().BeFalse();
    }

    [Fact]
    public async Task UnloadForUpdateAsync_UnknownId_ReturnsFalse()
    {
        bool result = await _lifecycle.UnloadForUpdateAsync(Ulid.NewUlid());

        result.Should().BeFalse();
    }

    // ── UninstallPluginAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UninstallPluginAsync_UnknownId_ThrowsInvalidOperation()
    {
        Func<Task> act = () => _lifecycle.UninstallPluginAsync(Ulid.NewUlid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UninstallPluginAsync_RemovesFromRegistry_DisposesInstance_TransitionsToDeleted()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active), plugin, null);

        await _lifecycle.UninstallPluginAsync(id);

        _registry.TryGetValue(id, out _).Should().BeFalse();
        plugin.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task UninstallPluginAsync_ReleasesScheduledWork_WhilePluginStillInRegistry()
    {
        // A scheduled-task plugin's named per-job cron executors are removed
        // by PluginCronRegistrar reading this plugin's own Jobs list back out
        // of the registry — if the registry entry is already gone by the
        // time that runs, it finds nothing to remove and every named job
        // executor keeps firing against the disposed instance forever.
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active), plugin, null);
        bool wasStillRegisteredWhenReleased = false;

        PluginLifecycleManager lifecycle = new(
            _eventBus,
            new MinimalServiceProvider(),
            NullLogger.Instance,
            _tempDir,
            TestStorageHelper.CreateStorage(_tempDir),
            _registry,
            new PluginLoader(
                _eventBus,
                new MinimalServiceProvider(),
                NullLogger.Instance,
                _tempDir,
                TestStorageHelper.CreateStorage(_tempDir),
                _registry,
                new PluginVerifier(),
                new PluginConsentService(new InMemoryConsentStore()),
                TestPluginPlatform.ContextFactory(
                    _eventBus,
                    TestStorageHelper.CreateStorage(_tempDir)
                )
            ),
            TestPluginPlatform.ContextFactory(_eventBus, TestStorageHelper.CreateStorage(_tempDir)),
            releaseScheduledWork: releasedId =>
                wasStillRegisteredWhenReleased = _registry.TryGetValue(releasedId, out _)
        );

        await lifecycle.UninstallPluginAsync(id);

        wasStillRegisteredWhenReleased.Should().BeTrue();
    }

    [Fact]
    public async Task UninstallPluginAsync_NoAssemblyPath_DoesNotThrow()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active, assemblyPath: null), plugin, null);

        Func<Task> act = () => _lifecycle.UninstallPluginAsync(id);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UninstallPluginAsync_AssemblyDirectoryExists_DeletesIt()
    {
        Ulid id = Ulid.NewUlid();
        string pluginDir = Path.Combine(_tempDir, "SomePlugin");
        Directory.CreateDirectory(pluginDir);
        string assemblyPath = Path.Combine(pluginDir, "SomePlugin.dll");
        File.WriteAllBytes(assemblyPath, [1, 2, 3]);
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active, assemblyPath), plugin, null);

        await _lifecycle.UninstallPluginAsync(id);

        Directory.Exists(pluginDir).Should().BeFalse();
    }

    [Fact]
    public async Task UninstallPluginAsync_AssemblyDirectoryAlreadyGone_DoesNotThrow()
    {
        Ulid id = Ulid.NewUlid();
        string assemblyPath = Path.Combine(_tempDir, "GoneAlready", "GoneAlready.dll");
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active, assemblyPath), plugin, null);

        Func<Task> act = () => _lifecycle.UninstallPluginAsync(id);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UninstallPluginAsync_NullInstance_DoesNotThrow()
    {
        Ulid id = Ulid.NewUlid();
        _registry[id] = new(Info(id, PluginStatus.Active), null, null);

        Func<Task> act = () => _lifecycle.UninstallPluginAsync(id);

        await act.Should().NotThrowAsync();
        _registry.TryGetValue(id, out _).Should().BeFalse();
    }

    [Fact]
    public async Task UninstallPluginAsync_RealLoadContext_UnloadsIt()
    {
        Ulid id = Ulid.NewUlid();
        string dummyPath = Path.Combine(_tempDir, $"unload-target-{Ulid.NewUlid():N}.dll");
        File.WriteAllBytes(dummyPath, []);
        PluginLoadContext loadContext = new(dummyPath);
        bool unloaded = false;
        loadContext.Unloading += _ => unloaded = true;
        _registry[id] = new(Info(id, PluginStatus.Active), null, loadContext);

        await _lifecycle.UninstallPluginAsync(id);

        unloaded.Should().BeTrue();
    }

    [Fact]
    public async Task UninstallPluginAsync_AssemblyPathIsARootPath_DoesNotThrow()
    {
        // Path.GetDirectoryName returns null ONLY for a bare root path (e.g.
        // the OS directory separator itself) — NOT for a directory-less
        // filename like "bare.dll", which resolves to "" (empty, non-null).
        // The `pluginDir is not null` half of this guard exists specifically
        // for this root-path shape.
        Ulid id = Ulid.NewUlid();
        string rootPath = Path.DirectorySeparatorChar.ToString();
        Path.GetDirectoryName(rootPath).Should().BeNull("this is exactly the edge case under test");
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active, assemblyPath: rootPath), plugin, null);

        Func<Task> act = () => _lifecycle.UninstallPluginAsync(id);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UninstallPluginAsync_DeleteDirectoryFailsWithAccessDenied_LogsWarningInsteadOfThrowing()
    {
        // Distinct exception type from the IOException test below: a read-only
        // file inside the directory makes Directory.Delete(recursive: true)
        // throw UnauthorizedAccessException specifically, deterministically —
        // this is what Windows actually throws for a plugin assembly that was
        // just Unload()ed but not yet garbage-collected (a real, non-contrived
        // race during a genuine uninstall), which a bare `catch (IOException)`
        // does not cover.
        Ulid id = Ulid.NewUlid();
        string pluginDir = Path.Combine(_tempDir, "ReadOnlyPlugin");
        Directory.CreateDirectory(pluginDir);
        string assemblyPath = Path.Combine(pluginDir, "ReadOnlyPlugin.dll");
        string readOnlyFilePath = Path.Combine(pluginDir, "readonly.bin");
        File.WriteAllBytes(assemblyPath, [1, 2, 3]);
        File.WriteAllBytes(readOnlyFilePath, [4, 5, 6]);
        File.SetAttributes(readOnlyFilePath, FileAttributes.ReadOnly);
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active, assemblyPath), plugin, null);

        Func<Task> act = () => _lifecycle.UninstallPluginAsync(id);

        await act.Should().NotThrowAsync();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            // The read-only attribute blocks Directory.Delete but not a rename —
            // renaming a directory never touches the permission bits of the
            // files inside it — so the directory is quarantined rather than
            // left sitting under its own name.
            AssertQuarantined(pluginDir, "ReadOnlyPlugin");
        else
            Directory
                .Exists(pluginDir)
                .Should()
                .BeFalse("POSIX ignores the read-only attribute, so the delete just succeeds");
    }

    [Fact]
    public async Task UninstallPluginAsync_DeleteDirectoryFails_LogsWarningInsteadOfThrowing()
    {
        Ulid id = Ulid.NewUlid();
        string pluginDir = Path.Combine(_tempDir, "LockedPlugin");
        Directory.CreateDirectory(pluginDir);
        string assemblyPath = Path.Combine(pluginDir, "LockedPlugin.dll");
        string lockedFilePath = Path.Combine(pluginDir, "locked.bin");
        File.WriteAllBytes(assemblyPath, [1, 2, 3]);
        File.WriteAllBytes(lockedFilePath, [4, 5, 6]);
        FakePlugin plugin = new();
        _registry[id] = new(Info(id, PluginStatus.Active, assemblyPath), plugin, null);

        // Hold an exclusive, non-shared handle open on a file inside the plugin
        // directory for the duration of the delete attempt — the only real way
        // to make Directory.Delete(recursive: true) throw IOException rather
        // than fabricating the exception directly.
        using FileStream lockHandle = new(
            lockedFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );

        Func<Task> act = () => _lifecycle.UninstallPluginAsync(id);

        await act.Should().NotThrowAsync();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            // Unlike the read-only case above, an actively open exclusive
            // handle blocks a directory rename too — the handle is still open
            // when the rename is attempted, so there is genuinely nothing left
            // to try from inside this process. This is the rare case
            // DeleteOrQueueForDeletionAsync's own doc comment names: the
            // directory is left exactly where it was, same as before this
            // feature existed, and a future disk-space audit is the backstop.
            Directory
                .Exists(pluginDir)
                .Should()
                .BeTrue("the handle was still open when the quarantine rename was attempted too");
        else
            Directory
                .Exists(pluginDir)
                .Should()
                .BeFalse("POSIX unlinks open files regardless, so the delete just succeeds");
    }

    /// <summary>
    /// Asserts a blocked uninstall moved the plugin's directory into
    /// <see cref="PluginManager.PendingDeletesFolder"/> instead of leaving it
    /// under its own name — the quarantine that makes "never leaves it behind"
    /// true even when an immediate delete cannot be.
    /// </summary>
    private void AssertQuarantined(string pluginDir, string pluginFolderName)
    {
        Directory
            .Exists(pluginDir)
            .Should()
            .BeFalse("uninstall must never leave the plugin's directory at its own path");

        string pendingDeletesDir = Path.Combine(_tempDir, PluginManager.PendingDeletesFolder);

        Directory.Exists(pendingDeletesDir).Should().BeTrue();
        Directory
            .GetDirectories(pendingDeletesDir, $"{pluginFolderName}-*")
            .Should()
            .ContainSingle(
                "the blocked directory should be waiting here for the next start to remove it"
            );
    }

    private sealed class FakePlugin : IPlugin
    {
        public int InitializeCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }

        public string Name => "fake";
        public string Description => "d";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0);

        public void Initialize(IPluginContext context) => InitializeCallCount++;

        public void Dispose() => DisposeCallCount++;
    }

    private sealed class ThrowingInitializePlugin : IPlugin
    {
        public string Name => "throwing";
        public string Description => "d";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0);

        public void Initialize(IPluginContext context) =>
            throw new ApplicationException("initialize boom");

        public void Dispose() { }
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
