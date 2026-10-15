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
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Logging;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.PluginSdk.Logging;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The server reads two registries: one names the console line and the run
/// file, the other is what the dashboard filters on. A plugin in only one of
/// them shows up nameless in the other.
/// </summary>
[Trait("Category", "Unit")]
public class PluginLoggersTests
{
    [Fact]
    public void Register_NamesThePluginInTheConsoleAndTheRunFile()
    {
        Ulid plugin = Ulid.NewUlid();

        new PluginLoggers(new NamingLoggerFactory()).Register(plugin, "Torrent Downloader");

        LogCategory category = LogCategories.Resolve(LogCategories.PluginKey(plugin));
        category.DisplayName.Should().Be("Torrent Downloader");
        category.Group.Should().Be("Plugins");
    }

    [Fact]
    public void Register_PutsThePluginInTheDashboardsPluginsGroup_InThePluginColour()
    {
        Ulid plugin = Ulid.NewUlid();

        new PluginLoggers(new NamingLoggerFactory()).Register(plugin, "Torrent Downloader");

        Logger.LogType type = Logger.LogTypes[LogCategories.PluginKey(plugin)];
        type.DisplayName.Should().Be("Torrent Downloader");
        type.Type.Should().Be("Plugins");
        type.ColorHex.Should().BeEquivalentTo(LogCategories.PluginDarkHex);
        Logger
            .LogTypes.Values.Should()
            .ContainSingle(entry => entry.Type == "spacer" && entry.DisplayName == "Plugins");
    }

    [Fact]
    public void For_IsTheLoggerNamedAfterThePluginsKey()
    {
        Ulid plugin = Ulid.NewUlid();
        NamingLoggerFactory factory = new();

        new PluginLoggers(factory).For(plugin);

        factory.Names.Should().Equal($"plugin:{plugin}");
    }

    private sealed class NamingLoggerFactory : ILoggerFactory
    {
        public List<string> Names { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            Names.Add(categoryName);
            return new ListLogger();
        }

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }
}
