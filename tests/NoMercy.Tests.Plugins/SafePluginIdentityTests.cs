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
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class SafePluginIdentityTests
{
    [Fact]
    public void Read_WellBehavedPlugin_ReturnsActualValues()
    {
        WellBehavedPlugin plugin = new();

        SafePluginIdentity identity = SafePluginIdentity.Read(plugin, plugin.GetType());

        identity.Id.Should().Be(WellBehavedPlugin.FixedId);
        identity.Name.Should().Be("Well Behaved");
        identity.Description.Should().Be("A normal plugin");
        identity.Version.Should().Be(new(1, 2, 3));
    }

    [Fact]
    public void Read_NullInstance_FallsBackToTypeName()
    {
        SafePluginIdentity identity = SafePluginIdentity.Read(null, typeof(WellBehavedPlugin));

        identity.Id.Should().Be(Ulid.Empty);
        identity.Name.Should().Be(typeof(WellBehavedPlugin).FullName);
        identity.Description.Should().BeEmpty();
        identity.Version.Should().Be(new(0, 0, 0));
    }

    [Fact]
    public void Read_ThrowingGetters_FallsBackToSafeDefaultsAndDoesNotThrow()
    {
        ThrowingPlugin plugin = new();

        Func<SafePluginIdentity> act = () => SafePluginIdentity.Read(plugin, plugin.GetType());

        SafePluginIdentity identity = act.Should().NotThrow().Subject;
        identity.Id.Should().Be(Ulid.Empty);
        identity.Name.Should().Be(typeof(ThrowingPlugin).FullName);
        identity.Description.Should().BeEmpty();
        identity.Version.Should().Be(new(0, 0, 0));
    }

    [Fact]
    public void Read_OpenGenericTypeParameter_FullNameIsNull_FallsBackToTypeName()
    {
        // Type.FullName is null for an open generic type parameter — the only
        // real (not fabricated) way to reach the `pluginType.FullName ?? name`
        // fallback, since every concrete, non-generic plugin type discovered
        // via Assembly.GetTypes() always has a non-null FullName.
        Type openGenericParameter = typeof(List<>).GetGenericArguments()[0];
        openGenericParameter.FullName.Should().BeNull("this is exactly the edge case under test");

        SafePluginIdentity identity = SafePluginIdentity.Read(null, openGenericParameter);

        identity.Name.Should().Be(openGenericParameter.Name);
    }

    [Fact]
    public void Read_GettersReturnNullWithoutThrowing_FallsBackToSafeDefaults()
    {
        // Distinct from ThrowingPlugin above: these getters succeed (no
        // exception at all) but hand back null despite IPlugin's non-nullable
        // signature — exercising the `instance.Description ?? string.Empty`
        // and `instance.Version ?? UnknownVersion` right-hand sides, which a
        // throwing getter can never reach (the throw happens before the `??`
        // is ever evaluated).
        NullReturningPlugin plugin = new();

        SafePluginIdentity identity = SafePluginIdentity.Read(plugin, plugin.GetType());

        identity.Id.Should().Be(NullReturningPlugin.FixedId);
        identity.Name.Should().Be("Null Returning");
        identity.Description.Should().BeEmpty();
        identity.Version.Should().Be(new(0, 0, 0));
    }

    private sealed class WellBehavedPlugin : IPlugin
    {
        public static readonly Ulid FixedId = Ulid.Parse("0H248H248H248H248H248H248H");

        public string Name => "Well Behaved";
        public string Description => "A normal plugin";
        public Ulid Id => FixedId;
        public Version Version => new(1, 2, 3);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }
    }

    private sealed class ThrowingPlugin : IPlugin
    {
        public string Name => throw new InvalidOperationException("name boom");
        public string Description => throw new InvalidOperationException("description boom");
        public Ulid Id => throw new InvalidOperationException("id boom");
        public Version Version => throw new InvalidOperationException("version boom");

        public void Initialize(IPluginContext context) =>
            throw new InvalidOperationException("init boom");

        public void Dispose() => throw new InvalidOperationException("dispose boom");
    }

    private sealed class NullReturningPlugin : IPlugin
    {
        public static readonly Ulid FixedId = Ulid.Parse("248H248H248H248H248H248H24");

        public string Name => "Null Returning";
        public string Description => null!;
        public Ulid Id => FixedId;
        public Version Version => null!;

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }
    }
}
