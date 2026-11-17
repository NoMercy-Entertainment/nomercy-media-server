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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Approval;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Provenance;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Consent, grants, approvals and install sources share one config file, and
/// a save is a read, merge and write of the whole file. Every store has to go
/// through one instance, or two saves at once each write back a file missing
/// the other's change.
/// </summary>
public class PluginPlatformConfigurationTests : IDisposable
{
    private const int Writes = 40;

    private readonly string _pluginsDir = Path.Combine(
        Path.GetTempPath(),
        $"nm-platform-config-{Ulid.NewUlid():N}"
    );

    public PluginPlatformConfigurationTests()
    {
        Directory.CreateDirectory(_pluginsDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (IOException) { }

        GC.SuppressFinalize(this);
    }

    private ServiceProvider Container()
    {
        ServiceCollection services = new();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddLogging();
        services.AddSingleton(TestStorageHelper.CreateBackend());
        services.AddPluginSystem(_pluginsDir);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

        return services.BuildServiceProvider();
    }

    private static Ulid Id(int index) => new(Guid.Parse($"00000000-0000-0000-0000-{index:D12}"));

    [Fact]
    public void The_container_hands_out_one_platform_configuration()
    {
        using ServiceProvider container = Container();

        container
            .GetRequiredService<IPluginPlatformConfiguration>()
            .Should()
            .BeSameAs(container.GetRequiredService<IPluginPlatformConfiguration>());
    }

    [Fact]
    public async Task Concurrent_saves_from_two_stores_keep_both_keys()
    {
        using ServiceProvider container = Container();
        IPluginGrantStore grants = container.GetRequiredService<IPluginGrantStore>();
        IPluginInstallSourceStore sources = container.GetRequiredService<IPluginInstallSourceStore>();
        IPluginApprovalStore approvals = container.GetRequiredService<IPluginApprovalStore>();

        await Task.WhenAll(
            Task.Run(() =>
            {
                for (int index = 0; index < Writes; index++)
                    grants.Grant(Id(index), PluginGrantKind.NetworkHost, "*");
            }),
            Task.Run(() =>
            {
                for (int index = 0; index < Writes; index++)
                    sources.Record(
                        Id(index),
                        new() { Kind = PluginInstallSourceKind.File }
                    );
            }),
            Task.Run(() =>
            {
                for (int index = 0; index < Writes; index++)
                    approvals.Approve($"Folder{index}", "digest");
            })
        );

        // Read back through a fresh instance: what is on disk, not a cache.
        PluginPlatformConfiguration disk = new(
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir)
        );
        ConfigPluginGrantStore grantsOnDisk = new(disk);
        ConfigPluginInstallSourceStore sourcesOnDisk = new(disk);
        ConfigPluginApprovalStore approvalsOnDisk = new(disk);

        for (int index = 0; index < Writes; index++)
        {
            grantsOnDisk.Holds(Id(index), PluginGrantKind.NetworkHost, "x").Should().BeTrue();
            sourcesOnDisk.Find(Id(index)).Should().NotBeNull();
            approvalsOnDisk.Find($"Folder{index}").Should().NotBeNull();
        }
    }
}
