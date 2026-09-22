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
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The facades this server puts on <see cref="IPluginContext" /> in place of the
/// host container, and the refusals a plugin meets when it reaches past them.
/// </summary>
public class PluginFacadeSurfaceTests
{
    public static TheoryData<string, Type> Facades =>
        new()
        {
            { "Net", typeof(IPluginNet) },
            { "Storage", typeof(IPluginStorage) },
            { "Process", typeof(IPluginProcess) },
            { "Server", typeof(IPluginServerInfo) },
            { "Native", typeof(IPluginNative) },
        };

    [Theory]
    [MemberData(nameof(Facades))]
    public void The_context_offers_the_facade(string member, Type contract)
    {
        PropertyInfo? property = typeof(IPluginContext).GetProperty(member);

        property.Should().NotBeNull(member);
        property!.PropertyType.Should().Be(contract);
    }

    [Fact]
    public void Dialling_without_the_capability_refuses_with_the_socket_code()
    {
        PluginRefusal refusal = PluginRefusalMessages.SocketUndeclared(
            "Torrent Downloader 0.4.1",
            "eztv.re",
            443
        );

        refusal.Code.Should().Be(PluginRefusalCodes.SocketUndeclared);
        refusal.Fix.Should().Contain("network.dial");
        refusal.Fix.Should().Contain("/nomercy-plugins/capabilities/network-dial");
    }

    [Fact]
    public void Writing_outside_a_grant_refuses_and_names_the_folder_capability()
    {
        PluginRefusal refusal = PluginRefusalMessages.FileOutsideGrant(
            "Torrent Downloader 0.4.1",
            "D:\\downloads\\intake"
        );

        refusal.Code.Should().Be(PluginRefusalCodes.FileOutsideGrant);
        refusal.Fix.Should().Contain("context.Storage.Private");
    }
}
