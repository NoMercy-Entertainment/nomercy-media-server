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
using NoMercy.PluginHost;
using NoMercy.PluginSdk;
using Xunit;

namespace NoMercy.Tests.PluginHost;

public class PluginHostLoadContextTests
{
    [Fact]
    public void TheHostLetsThroughEverySdkAssemblyTheServerShares()
    {
        // A plugin built from the template carries NoMercy.PluginSdk.Mvc. In
        // process the server shares it; out of process it must still load, or
        // moving a plugin across the boundary refuses it.
        IEnumerable<string> shared = PluginHostOptions.DefaultSharedAssemblies.Where(name =>
            name.StartsWith("NoMercy.", StringComparison.Ordinal)
        );

        shared.Should().OnlyContain(name => !PluginHostLoadContext.IsServerAssembly(name));
    }

    [Theory]
    [InlineData("NoMercy.Api")]
    [InlineData("NoMercy.Database")]
    [InlineData("nomercy.data")]
    [InlineData("NoMercy.Plugins")]
    [InlineData("NoMercy.PluginHost")]
    public void AServerAssemblyIsRefusedInTheHost(string name)
    {
        PluginHostLoadContext.IsServerAssembly(name).Should().BeTrue();
    }

    // A plugin's own second assembly (Automix.Analysis, TorrentDownloader.Core)
    // is plugin code: no server assembly carries the NoMercy.Plugin. prefix.
    [Theory]
    [InlineData("NoMercy.Plugin.Automix.Analysis")]
    [InlineData("NoMercy.Plugin.TorrentDownloader.Core")]
    public void APluginsOwnAssemblyIsNotAServerAssembly(string name)
    {
        PluginHostLoadContext.IsServerAssembly(name).Should().BeFalse();
    }
}
