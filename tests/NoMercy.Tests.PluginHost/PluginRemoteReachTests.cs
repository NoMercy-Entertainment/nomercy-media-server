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
using FluentAssertions;
using NoMercy.PluginHost;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// The table the dashboard reads has to say what the out-of-process runtime
/// really carries.
/// <para>
/// A stale entry is the failure the table exists to prevent: a facade added to
/// the contract and not carried would come back null in another process, and
/// the dashboard would tell the owner the plugin fits.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginRemoteReachTests
{
    [Fact]
    public void TheUncarriedFacades_AreExactlyTheContextMembersTheRemoteContextLeavesAtTheirDefault()
    {
        InterfaceMapping map = typeof(RemotePluginContext).GetInterfaceMap(typeof(IPluginContext));

        // Every member, not only the properties: a method with a default body
        // that the remote context does not override is just as absent there.
        List<string> leftAtDefault = map
            .InterfaceMethods.Where(
                (_, index) => map.TargetMethods[index].DeclaringType != typeof(RemotePluginContext)
            )
            .Select(MemberName)
            .Distinct()
            .ToList();

        PluginRemoteReach
            .UncarriedFacades.Keys.Should()
            .BeEquivalentTo(
                leftAtDefault,
                "a facade added to IPluginContext or carried by RemotePluginContext is listed in PluginRemoteReach in the same change"
            );
    }

    [Fact]
    public void EveryHookTheContractDeclares_IsClassified()
    {
        IEnumerable<string> classified = PluginRemoteReach
            .EntryPointHooks.Keys.Concat(PluginRemoteReach.CarriedHooks)
            .Concat(
                PluginRemoteReach
                    .UncarriedFacades.Values.Where(hook => hook is not null)
                    .Select(hook => hook!)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase);

        classified
            .Should()
            .BeEquivalentTo(
                PluginHookCapability.Baseline.Concat(PluginHookCapability.Elevated),
                "a new hook says whether it is an entry point, a carried facade or an uncarried one"
            );
    }

    [Fact]
    public void TheForwardedEntryPoints_AreExactlyTheOnesTheServerSideProxyImplements()
    {
        List<string> implemented = PluginRemoteReach
            .EntryPointHooks.Where(hook => hook.Value.IsAssignableFrom(typeof(RemotePlugin)))
            .Select(hook => hook.Key)
            .ToList();

        PluginRemoteReach.ForwardedHooks.Should().BeEquivalentTo(implemented);
    }

    [Fact]
    public void EveryForwardedHook_IsAnEntryPoint()
    {
        PluginRemoteReach
            .ForwardedHooks.Should()
            .OnlyContain(hook => PluginRemoteReach.EntryPointHooks.ContainsKey(hook));
    }

    [Fact]
    public void EveryEntryPointTheContractDeclares_IsListedWithItsHookOrAsHookless()
    {
        List<Type> entryPoints = typeof(IPlugin)
            .Assembly.GetExportedTypes()
            .Where(type =>
                type.IsInterface
                && type != typeof(IPlugin)
                && typeof(IPlugin).IsAssignableFrom(type)
            )
            .ToList();

        PluginRemoteReach
            .EntryPointHooks.Values.Concat(PluginRemoteReach.HooklessEntryPoints)
            .Should()
            .BeEquivalentTo(
                entryPoints,
                "a new entry point says which hook declares it, or that none does"
            );
    }

    [Fact]
    public void TheHooklessEntryPoints_AreOnesTheServerSideProxyDoesNotForward()
    {
        PluginRemoteReach
            .HooklessEntryPoints.Should()
            .OnlyContain(
                entryPoint => !entryPoint.IsAssignableFrom(typeof(RemotePlugin)),
                "the list names what a plugin in its own process loses"
            );
    }

    private static string MemberName(MethodInfo method) =>
        method.IsSpecialName && method.Name.StartsWith("get_", StringComparison.Ordinal)
            ? method.Name["get_".Length..]
            : method.Name;
}
