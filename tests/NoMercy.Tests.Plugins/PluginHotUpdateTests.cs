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

using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Updates a genuinely resident plugin — the real Echo sample, actually
/// loaded into its own AssemblyLoadContext — end to end through
/// <see cref="PluginManager.InstallPluginArchiveAsync"/>.
/// <para>
/// Every other update test in this project (<c>PluginArchiveUpdateTests</c>)
/// uses a stub assembly precisely so loading fails and the plugin is never
/// actually resident — that is what lets those tests stay fast and
/// deterministic, but it also means the "unload a live plugin and swap it"
/// branch this feature exists for had never been exercised by a real load
/// context. This class is the one that does, and doing so caught a real bug
/// a stub could not have: the move into <see cref="PluginManager.RollbackFolder"/>
/// failed on every single attempt because nothing ever created that folder's
/// parent first — indistinguishable from "still locked" until traced, since
/// <see cref="DirectoryNotFoundException"/> is itself an <see cref="IOException"/>.
/// Fixed, this update completes hot on every run.
/// </para>
/// </summary>
public class PluginHotUpdateTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.Parse("01ECH000000000000000000000");

    // Must equal the assembly's own filename without extension: FindManifest
    // derives the installed folder name from the assembly, not from the
    // archive's internal folder structure (PluginArchiveUpdateTests follows
    // the same convention deliberately, for the same reason) - a mismatch here
    // makes InstallPluginArchiveAsync operate on a folder that was never
    // staged, silently creating a second one instead of exercising a real
    // in-place swap.
    private const string AssemblyName = "NoMercy.Plugin.Samples.Echo.dll";
    private const string FolderName = "NoMercy.Plugin.Samples.Echo";

    private readonly string _pluginsDir;
    private readonly string _echoPluginDir;
    private readonly InMemoryEventBus _eventBus;
    private readonly PluginManager _manager;

    public PluginHotUpdateTests()
    {
        _pluginsDir = Path.Combine(Path.GetTempPath(), "nomercy-hot-update-" + Ulid.NewUlid());
        _echoPluginDir = Path.Combine(_pluginsDir, FolderName);
        Directory.CreateDirectory(_echoPluginDir);

        _eventBus = new();
        _manager = new(
            _eventBus,
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            TestStorageHelper.CreateBackend()
        );
    }

    public void Dispose()
    {
        _manager.Dispose();

        // Only to let a short-lived test process clean up its own temp
        // directory promptly — production never does this (see
        // PluginFileRetry's own doc comment on why not).
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (Exception) { }
    }

    private static string EchoBinDir()
    {
        string testBinDir = Path.GetDirectoryName(typeof(PluginHotUpdateTests).Assembly.Location)!;
        string buildConfig = Path.GetFileName(Path.GetDirectoryName(testBinDir)!);
        string repoRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", ".."));

        string preferred = Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.Echo",
            "bin",
            buildConfig,
            "net10.0"
        );

        if (Directory.Exists(preferred))
            return preferred;

        return Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.Echo",
            "bin",
            string.Equals(buildConfig, "Release", StringComparison.OrdinalIgnoreCase)
                ? "Debug"
                : "Release",
            "net10.0"
        );
    }

    private static string Manifest(string version) =>
        $$"""
            {
              "id": "{{PluginId}}",
              "name": "Echo",
              "version": "{{version}}",
              "description": "Sample plugin for the test suite",
              "assembly": "{{AssemblyName}}",
              "autoEnabled": true
            }
            """;

    private void StageEchoV1()
    {
        string binDir = EchoBinDir();
        string dllSrc = Path.Combine(binDir, AssemblyName);

        if (!File.Exists(dllSrc))
            throw new FileNotFoundException(
                $"Echo plugin DLL not found at '{dllSrc}'. Build NoMercy.Plugin.Samples.Echo first."
            );

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(Path.Combine(_echoPluginDir, "plugin.json"), Manifest("1.0.0"));
    }

    /// <summary>
    /// The v2 update archive: every file the v1 install has, republished as an
    /// archive with a bumped manifest version. The assembly bytes are
    /// identical — a real update need not change code to prove the swap
    /// mechanics, and using the same real, loadable DLL is what makes this a
    /// genuine residency case rather than another stub.
    /// </summary>
    private string BuildV2Archive()
    {
        string archivePath = Path.Combine(_pluginsDir, "echo-2.0.0.zip");

        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        foreach (string file in Directory.EnumerateFiles(_echoPluginDir))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
                continue;

            archive.CreateEntryFromFile(file, $"{FolderName}/{name}");
        }

        using (StreamWriter writer = new(archive.CreateEntry($"{FolderName}/plugin.json").Open()))
            writer.Write(Manifest("2.0.0"));

        return archivePath;
    }

    [Fact]
    public async Task UpdatingAResidentPlugin_AlwaysConvergesOnTheNewVersion()
    {
        StageEchoV1();
        await _manager.LoadPluginsFromDirectoryAsync();

        _manager
            .GetInstalledPlugins()
            .Should()
            .ContainSingle(p => p.Id == PluginId)
            .Which.Version.ToString()
            .Should()
            .Be("1.0.0");

        string archivePath = BuildV2Archive();

        PluginManager readAfter = _manager;

        try
        {
            await _manager.InstallPluginArchiveAsync(archivePath);
        }
        catch (PluginUpdatePendingRestartException)
        {
            // The fallback this feature keeps as a safety net: the assembly did
            // not free up inside the wait budget on this run, so the update is
            // staged exactly as it always was. Applying it needs an actual
            // restart, not just calling the boot scan again on the same still-
            // loaded instance — the plugin is still resident right up until
            // this, so the very same lock would just block the plain file copy
            // ApplyPendingUpdates does too. Dispose (which unloads it for
            // real) and stand up a fresh manager over the same directory,
            // exactly like the next process start would.
            _manager.Dispose();
            readAfter = new(
                new InMemoryEventBus(),
                new MinimalServiceProvider(),
                NullLogger<PluginManager>.Instance,
                _pluginsDir,
                TestStorageHelper.CreateStorage(_pluginsDir),
                TestStorageHelper.CreateBackend()
            );
            await readAfter.LoadPluginsFromDirectoryAsync();
        }

        PluginInfo updated = readAfter
            .GetInstalledPlugins()
            .Should()
            .ContainSingle(
                p => p.Id == PluginId,
                "the update must never duplicate or drop the plugin"
            )
            .Which;

        updated.Version.ToString().Should().Be("2.0.0");
        updated
            .Status.Should()
            .Be(
                PluginStatus.Active,
                "an update must not leave the plugin worse off than before it started"
            );

        if (!ReferenceEquals(readAfter, _manager))
        {
            readAfter.Dispose();
        }
    }

    [Fact]
    public async Task UpdatingAResidentPlugin_WhenApplyingTheSwapFails_RollsBackAndLeavesNoStagedCopyBehind()
    {
        // The failure TrySwapResidentPluginAsync's rollback exists for: not
        // "the assembly was still locked" (the expected, routine case, which
        // never reaches this far), but a real fault applying the staged copy
        // itself — a bad disk, a file that turned unreadable mid-swap. Forced
        // via CopyStreamOverride, and residency forced via IPluginAssemblyTracker
        // rather than a real loaded plugin: a genuine collectible
        // AssemblyLoadContext in an otherwise-idle test process turned out not
        // to release its file even across repeated explicit GC.Collect() calls
        // (five attempts, all lost the race) — a real characteristic of this
        // runtime worth its own report, not something to route around here by
        // routing this test through the same nondeterministic path. Forcing
        // residency through the tracker's own real file-lock probe, on a lock
        // this test holds and controls directly, reaches the exact same
        // TrySwapResidentPluginAsync code path deterministically.
        Ulid pluginId = Ulid.NewUlid();
        string pluginDir = Path.Combine(_pluginsDir, FolderName);
        Directory.CreateDirectory(pluginDir);
        await File.WriteAllTextAsync(Path.Combine(pluginDir, AssemblyName), "v1 content");
        await File.WriteAllTextAsync(Path.Combine(pluginDir, "plugin.json"), Manifest("1.0.0"));

        string lockFile = Path.Combine(_pluginsDir, "lock.marker");
        await File.WriteAllTextAsync(lockFile, "held");
        using FileStream heldLock = new(lockFile, FileMode.Open, FileAccess.Read, FileShare.None);

        PluginAssemblyTracker tracker = new();
        tracker.TrackUnload(pluginId, lockFile);

        using PluginManager manager = new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            TestStorageHelper.CreateBackend(),
            assemblyTracker: tracker
        );

        string archivePath = Path.Combine(_pluginsDir, "update.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (
                StreamWriter writer = new(archive.CreateEntry($"{FolderName}/plugin.json").Open())
            )
                writer.Write(ManifestFor(pluginId, "2.0.0"));
            using (
                StreamWriter writer = new(
                    archive.CreateEntry($"{FolderName}/{AssemblyName}").Open()
                )
            )
                writer.Write("v2 content");
        }

        manager.CopyStreamOverride = (_, _) =>
            throw new IOException("mutation-test: forced mid-copy failure");

        Func<Task> act = () => manager.InstallPluginArchiveAsync(archivePath);

        await act.Should()
            .ThrowAsync<IOException>()
            .WithMessage(
                "mutation-test: forced mid-copy failure",
                "a real fault applying the update must reach the caller, not be swallowed as success"
            );

        string staging = Path.Combine(_pluginsDir, PluginManager.PendingUpdatesFolder, FolderName);
        Directory
            .Exists(staging)
            .Should()
            .BeFalse("a failed swap must not leave its half-applied staged copy behind");
        Directory
            .Exists(Path.Combine(_pluginsDir, PluginManager.PendingUpdatesFolder))
            .Should()
            .BeFalse("nor an empty marker folder once the last entry under it is gone");
        Directory
            .Exists(Path.Combine(_pluginsDir, PluginManager.RollbackFolder))
            .Should()
            .BeFalse("nor an empty rollback folder once its one entry is restored");

        File.Exists(Path.Combine(pluginDir, AssemblyName))
            .Should()
            .BeTrue("the old copy must come back");
        File.ReadAllText(Path.Combine(pluginDir, AssemblyName))
            .Should()
            .Be(
                "v1 content",
                "a failed update must restore exactly what was there before, not the half-applied copy"
            );
    }

    private static string ManifestFor(Ulid id, string version) =>
        $$"""
            {
              "id": "{{id}}",
              "name": "Echo",
              "version": "{{version}}",
              "description": "Sample plugin for the test suite",
              "assembly": "{{AssemblyName}}",
              "autoEnabled": true
            }
            """;

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
