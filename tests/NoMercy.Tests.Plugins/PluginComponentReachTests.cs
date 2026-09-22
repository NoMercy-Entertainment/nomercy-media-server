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
using NoMercy.Design;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Every component this server can draw, a plugin can name.
///
/// <para>
/// Two sets: the design system's fifty-six, and the media components the app
/// builds its own home, library and music screens from. Both used to live where
/// a plugin could not reference them, so a plugin could name ten components in
/// total and the rest of the platform was closed to it.
/// </para>
/// </summary>
public class PluginComponentReachTests
{
    [Fact]
    public void APluginCanNameEveryComponentTheAppDrawsItsOwnScreensWith()
    {
        foreach (string component in NmAppComponents.All)
        {
            PluginComponent node = new() { Id = "x", Component = component };

            node.Component.Should().Be(component);
        }
    }

    // This project cannot see NoMercy.Api at all, which is the isolation being
    // asserted: everything a plugin needs is reachable without it. That the app's
    // own ComponentTypes still spells these names identically is checked from the
    // API side, where both are visible.
    //
    // The whole point of the move: a plugin holds the design system's records and
    // the app's component names in one place, without referencing the web project.
    [Fact]
    public void BothSetsAreReachableFromThePluginContract()
    {
        NmKitchenSink.Components.Should().HaveCount(57);
        NmAppComponents.All.Should().HaveCount(14);
    }
}
