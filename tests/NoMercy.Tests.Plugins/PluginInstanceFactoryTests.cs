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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginInstanceFactoryTests
{
    [Fact]
    public void Create_InjectsRegisteredDependencyIntoConstructor()
    {
        ServiceCollection services = new();
        services.AddSingleton(new Dependency("injected"));
        using ServiceProvider provider = services.BuildServiceProvider();

        IPlugin plugin = PluginInstanceFactory.Create(provider, typeof(DependentPlugin));

        plugin.Should().BeOfType<DependentPlugin>();
        ((DependentPlugin)plugin).Dependency.Value.Should().Be("injected");
    }

    [Fact]
    public void Create_WorksForParameterlessConstructor()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();

        IPlugin plugin = PluginInstanceFactory.Create(provider, typeof(SimplePlugin));

        plugin.Should().BeOfType<SimplePlugin>();
    }

    [Fact]
    public void Create_ThrowsWhenAConstructorDependencyIsNotRegistered()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();

        Action act = () => PluginInstanceFactory.Create(provider, typeof(DependentPlugin));

        act.Should().Throw<InvalidOperationException>();
    }

    private sealed class Dependency(string value)
    {
        public string Value { get; } = value;
    }

    private sealed class DependentPlugin(Dependency dependency) : IPlugin
    {
        public Dependency Dependency { get; } = dependency;
        public string Name => "Dependent";
        public string Description => string.Empty;
        public Ulid Id => Ulid.Parse("1248H248H248H248H248H248H2");
        public Version Version => new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }
    }

    private sealed class SimplePlugin : IPlugin
    {
        public string Name => "Simple";
        public string Description => string.Empty;
        public Ulid Id => Ulid.Parse("1K6CSK6CSK6CSK6CSK6CSK6CSK");
        public Version Version => new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }
    }
}
