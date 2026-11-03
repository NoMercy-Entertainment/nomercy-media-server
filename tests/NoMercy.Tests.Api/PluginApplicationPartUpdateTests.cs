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

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NoMercy.Api.Plugins;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// An update loads the new copy of a plugin in a new load context, beside the
/// old one, under the same id. MVC has to serve the new copy's controllers:
/// the old ones ask the plugin manager for their plugin, get the new instance,
/// and cannot recognise it (same class name, two load contexts).
/// <para>
/// The second copy here is this test assembly loaded a second time, into its
/// own load context. It carries a plugin controller
/// (<see cref="SampleRoutingPluginController"/>) and <see cref="SwappablePlugin"/>,
/// so it stands in for a plugin release with REST endpoints.
/// </para>
/// </summary>
public class PluginApplicationPartUpdateTests
{
    private static readonly Ulid PluginId = Ulid.Parse(SwappablePlugin.IdText);

    private static PluginInfo Info() =>
        new()
        {
            Id = PluginId,
            Name = "Swappable",
            Description = "Carries one REST controller.",
            Version = new(1, 0),
            Status = PluginStatus.Active,
            Capabilities = new() { Rest = true },
        };

    private static IPlugin InstanceFromASecondCopy()
    {
        AssemblyLoadContext context = new("plugin-update-" + Ulid.NewUlid());
        Assembly copy = context.LoadFromAssemblyPath(typeof(SwappablePlugin).Assembly.Location);
        Type type = copy.GetType(typeof(SwappablePlugin).FullName!, throwOnError: true)!;

        return (IPlugin)Activator.CreateInstance(type)!;
    }

    private static List<Assembly> AttachedAssemblies(ApplicationPartManager partManager) =>
        partManager.ApplicationParts.OfType<AssemblyPart>().Select(part => part.Assembly).ToList();

    [Fact]
    public void AttachingAnUpdatedCopy_ServesTheNewAssemblysControllersAndRefreshesTheRoutes()
    {
        ApplicationPartManager partManager = new();
        PluginActionDescriptorChangeProvider changeProvider = new();
        PluginApplicationPartRegistrar registrar = new(
            partManager,
            changeProvider,
            NullLogger<PluginApplicationPartRegistrar>.Instance
        );
        SwappingPluginManager manager = new(new SwappablePlugin());
        registrar.Attach(Info(), manager);
        Assembly oldAssembly = typeof(SwappablePlugin).Assembly;

        manager.Instance = InstanceFromASecondCopy();
        Assembly newAssembly = manager.Instance.GetType().Assembly;
        IChangeToken token = changeProvider.GetChangeToken();
        bool attached = registrar.Attach(Info(), manager);

        newAssembly.Should().NotBeSameAs(oldAssembly);
        AttachedAssemblies(partManager)
            .Should()
            .Equal([newAssembly], "the updated copy's controllers replace the old copy's");
        attached.Should().BeTrue();
        registrar.OwnerOf(newAssembly).Should().Be(PluginId);
        registrar.OwnerOf(oldAssembly).Should().BeNull();
        token.HasChanged.Should().BeTrue("MVC must re-read its controllers after the swap");
    }

    [Fact]
    public void AttachingTheSameCopyAgain_ChangesNothing()
    {
        ApplicationPartManager partManager = new();
        PluginActionDescriptorChangeProvider changeProvider = new();
        PluginApplicationPartRegistrar registrar = new(
            partManager,
            changeProvider,
            NullLogger<PluginApplicationPartRegistrar>.Instance
        );
        SwappingPluginManager manager = new(new SwappablePlugin());
        registrar.Attach(Info(), manager);
        IChangeToken token = changeProvider.GetChangeToken();

        bool attached = registrar.Attach(Info(), manager);

        attached.Should().BeFalse();
        AttachedAssemblies(partManager).Should().Equal([typeof(SwappablePlugin).Assembly]);
        token.HasChanged.Should().BeFalse();
    }

    private sealed class SwappingPluginManager(IPlugin instance) : IPluginManager
    {
        public IPlugin Instance { get; set; } = instance;

        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [Info()];

        public IPlugin? GetPluginInstance(Ulid pluginId) => pluginId == PluginId ? Instance : null;

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

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
}

/// <summary>
/// Public with a parameterless constructor so a second copy of this assembly
/// can create one. The id is a constant: a static field would differ between
/// the two copies.
/// </summary>
public sealed class SwappablePlugin : IPlugin
{
    public const string IdText = "01JBNM5WAPPAB1EP1VG1N00001";

    public string Name => "Swappable";
    public string Description => "Carries one REST controller.";
    public Ulid Id => Ulid.Parse(IdText);
    public Version Version => new(1, 0);

    public void Initialize(IPluginContext context) { }

    public void Dispose() { }
}
