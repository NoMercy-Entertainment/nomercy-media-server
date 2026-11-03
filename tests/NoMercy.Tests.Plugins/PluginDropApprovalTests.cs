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
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Approval;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin folder the server did not put there itself: no marketplace
/// checksum, no dashboard upload, just files that appeared. Issue #622: such
/// a folder loaded at the next start exactly like an install, so any file
/// write into the plugins folder was code running in the server process.
/// </summary>
public class PluginDropApprovalTests : IDisposable
{
    private const string Echo = "NoMercy.Plugin.Samples.Echo";
    private static readonly Ulid EchoId = Ulid.Parse("01JDROPECH0000000000000000");

    private readonly string _tempPluginsDir;
    private readonly List<PluginManager> _managers = [];

    public PluginDropApprovalTests()
    {
        _tempPluginsDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-drop-approval-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempPluginsDir);
    }

    public void Dispose()
    {
        foreach (PluginManager manager in _managers)
            manager.Dispose();

        // The Echo plugin is loaded for real; its shadow copy is only free
        // once the load context is collected.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_tempPluginsDir))
                Directory.Delete(_tempPluginsDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup
        }
    }

    /// <summary>One server start over the plugins folder.</summary>
    private PluginManager Boot()
    {
        PluginManager manager = new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            TestStorageHelper.CreateBackend()
        );
        _managers.Add(manager);

        return manager;
    }

    private static string EchoManifest() =>
        $$"""
            {
              "id": "{{EchoId}}",
              "name": "Echo",
              "version": "1.2.0",
              "description": "echoes",
              "assembly": "{{Echo}}.dll",
              "autoEnabled": true
            }
            """;

    /// <summary>
    /// The Echo DLL copied into the plugins folder by hand: no record, no
    /// marker, nothing the server wrote. Returns the DLL's path.
    /// </summary>
    private string DropEcho(bool withManifest = true)
    {
        string pluginDir = Path.Combine(_tempPluginsDir, "Echo");
        Directory.CreateDirectory(pluginDir);
        string dll = Path.Combine(pluginDir, $"{Echo}.dll");
        File.Copy(CodeScanVerificationStageTests.SampleDllPath(Echo), dll);
        if (withManifest)
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), EchoManifest());

        return dll;
    }

    /// <summary>A release zip of Echo, the shape the marketplace serves.</summary>
    private string EchoArchive()
    {
        string path = Path.Combine(_tempPluginsDir, "..", $"echo-{Ulid.NewUlid()}.zip");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(
            CodeScanVerificationStageTests.SampleDllPath(Echo),
            $"Echo/{Echo}.dll"
        );
        using StreamWriter manifest = new(archive.CreateEntry("Echo/plugin.json").Open());
        manifest.Write(EchoManifest());

        return Path.GetFullPath(path);
    }

    private static PluginInfo Only(PluginManager manager) =>
        manager.GetInstalledPlugins().Should().ContainSingle().Subject;

    [Fact]
    public async Task AHandDroppedPluginIsHeldUntilTheOwnerApproves()
    {
        DropEcho();
        PluginManager manager = Boot();

        IReadOnlyList<PluginLoadResult> results = await manager.LoadAllAsync();

        results
            .Should()
            .BeEmpty("a plugin the server did not install itself waits for the owner's approval");
        PluginInfo held = Only(manager);
        held.Id.Should().Be(EchoId, "the dashboard shows the held plugin under its own id");
        held.Status.Should().Be(PluginStatus.Disabled);
        held.ApprovalReason.Should().Be(PluginDropGate.NeverApproved);
        manager.GetPluginInstance(EchoId).Should().BeNull("not a byte of it was mapped");
    }

    [Fact]
    public async Task ApprovingLoadsThePluginAndTheApprovalSurvivesARestart()
    {
        DropEcho();
        PluginManager first = Boot();
        await first.LoadAllAsync();

        await first.ApprovePluginAsync(EchoId);

        PluginInfo approved = Only(first);
        approved.Status.Should().Be(PluginStatus.Active);
        approved.ApprovalReason.Should().BeNull();
        first.GetPluginInstance(EchoId).Should().NotBeNull();
        first.Dispose();

        PluginManager second = Boot();
        IReadOnlyList<PluginLoadResult> results = await second.LoadAllAsync();

        results
            .Should()
            .ContainSingle("the owner's yes is kept across starts")
            .Which.PluginId.Should()
            .Be(EchoId);
        Only(second).ApprovalReason.Should().BeNull();
    }

    [Fact]
    public async Task AChangedFileAfterApprovalIsHeldAgain()
    {
        string dll = DropEcho();
        PluginManager first = Boot();
        await first.LoadAllAsync();
        await first.ApprovePluginAsync(EchoId);
        first.Dispose();

        await File.AppendAllTextAsync(dll, "x");
        PluginManager second = Boot();
        IReadOnlyList<PluginLoadResult> results = await second.LoadAllAsync();

        results.Should().BeEmpty("the folder is no longer the one the owner approved");
        PluginInfo held = Only(second);
        held.Status.Should().Be(PluginStatus.Disabled);
        held.ApprovalReason.Should().Be(PluginDropGate.ChangedSinceApproval);
        second.GetPluginInstance(EchoId).Should().BeNull();
    }

    [Fact]
    public async Task ApprovingTheChangedFolderLoadsItAgain()
    {
        string dll = DropEcho();
        PluginManager first = Boot();
        await first.LoadAllAsync();
        await first.ApprovePluginAsync(EchoId);
        first.Dispose();

        await File.AppendAllTextAsync(dll, "x");
        PluginManager second = Boot();
        await second.LoadAllAsync();
        await second.ApprovePluginAsync(EchoId);

        Only(second).Status.Should().Be(PluginStatus.Active, "the second yes covers the new files");
    }

    [Fact]
    public async Task APluginTheServerInstalledItselfLoadsWithoutApproval()
    {
        PluginManager installer = Boot();
        await installer.InstallPluginArchiveAsync(EchoArchive());
        installer.Dispose();

        PluginManager second = Boot();
        IReadOnlyList<PluginLoadResult> results = await second.LoadAllAsync();

        results
            .Should()
            .ContainSingle("an install the server checked is not a drop")
            .Which.PluginId.Should()
            .Be(EchoId);
        Only(second).ApprovalReason.Should().BeNull();
    }

    [Fact]
    public async Task EnablingAHeldPluginIsRefusedAndLoadsNothing()
    {
        DropEcho();
        PluginManager manager = Boot();
        await manager.LoadAllAsync();

        Func<Task> enable = () => manager.EnablePluginAsync(EchoId);

        await enable
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*waiting for the owner's approval*");
        manager.GetPluginInstance(EchoId).Should().BeNull("enable is not a door past the hold");
        Only(manager).Status.Should().Be(PluginStatus.Disabled);
    }

    [Fact]
    public async Task ApprovingAPluginThatIsNotWaitingIsRefused()
    {
        PluginManager installer = Boot();
        await installer.InstallPluginArchiveAsync(EchoArchive());

        Func<Task> approve = () => installer.ApprovePluginAsync(EchoId);

        await approve
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*not waiting for approval*");
    }

    [Fact]
    public async Task AHandDroppedBareAssemblyIsHeldAndApprovingItLoadsIt()
    {
        DropEcho(withManifest: false);
        PluginManager manager = Boot();

        IReadOnlyList<PluginLoadResult> results = await manager.LoadAllAsync();

        results.Should().BeEmpty("a bare assembly dropped by hand is a drop like any other");
        PluginInfo held = Only(manager);
        held.Name.Should().Be("Echo", "with no manifest, the folder is the only name it has");
        held.ApprovalReason.Should().Be(PluginDropGate.NeverApproved);

        await manager.ApprovePluginAsync(held.Id);

        PluginInfo loaded = Only(manager);
        loaded.Status.Should().Be(PluginStatus.Active);
        loaded.Id.Should().NotBe(held.Id, "once it runs, the assembly says who it is");
        manager.GetPluginInstance(loaded.Id).Should().NotBeNull();
    }

    [Fact]
    public async Task UninstallingAnApprovedDropForgetsTheApproval()
    {
        DropEcho();
        PluginManager first = Boot();
        await first.LoadAllAsync();
        await first.ApprovePluginAsync(EchoId);

        await first.UninstallPluginAsync(EchoId);
        first.Dispose();

        DropEcho();
        PluginManager second = Boot();
        IReadOnlyList<PluginLoadResult> results = await second.LoadAllAsync();

        results.Should().BeEmpty("the same files dropped again are a new drop");
        Only(second).ApprovalReason.Should().Be(PluginDropGate.NeverApproved);
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
