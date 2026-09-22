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
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The switch that moves plugins out of the server's process.
/// <para>
/// Two questions, not one: moving every plugin at once is the migration, and
/// moving a single plugin is how somebody tries the new runtime on one they
/// can afford to have stop.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginRuntimeModeTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-runtime-mode-" + Ulid.NewUlid()
    );

    [Fact]
    public void AServerNobodyConfigured_RunsPluginsWhereItAlwaysDid()
    {
        PluginRuntimeMode.Load(_folder).For(Ulid.NewUlid()).Should().Be(PluginIsolation.InProcess);
    }

    /// <summary>
    /// The out-of-process runtime is the safer place for a plugin, but it is
    /// the newer path. Defaulting a working server onto it because a settings
    /// file would not parse turns a bad read into every plugin behaving
    /// differently at once.
    /// </summary>
    [Fact]
    public void AnUnreadableSetting_LeavesPluginsWhereTheyWere()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "runtime-mode.json"), "{ this is not json");

        PluginRuntimeMode.Load(_folder).Default.Should().Be(PluginIsolation.InProcess);
    }

    [Fact]
    public void TheGlobalSwitch_MovesEveryPluginThatHasNoAnswerOfItsOwn()
    {
        new PluginRuntimeMode(PluginIsolation.OutOfProcess).Save(_folder);

        PluginRuntimeMode
            .Load(_folder)
            .For(Ulid.NewUlid())
            .Should()
            .Be(PluginIsolation.OutOfProcess);
    }

    [Fact]
    public void OnePluginsAnswer_BeatsTheGlobalOne()
    {
        Ulid kept = Ulid.NewUlid();

        new PluginRuntimeMode(
            PluginIsolation.OutOfProcess,
            new Dictionary<string, PluginIsolation>
            {
                [kept.ToString()] = PluginIsolation.InProcess,
            }
        ).Save(_folder);

        PluginRuntimeMode loaded = PluginRuntimeMode.Load(_folder);

        loaded.For(kept).Should().Be(PluginIsolation.InProcess);
        loaded.For(Ulid.NewUlid()).Should().Be(PluginIsolation.OutOfProcess);
    }

    [Fact]
    public void TheSettingSurvivesBeingWrittenAndReadBack()
    {
        Ulid moved = Ulid.NewUlid();

        new PluginRuntimeMode(
            PluginIsolation.InProcess,
            new Dictionary<string, PluginIsolation>
            {
                [moved.ToString()] = PluginIsolation.OutOfProcess,
            }
        ).Save(_folder);

        PluginRuntimeMode.Load(_folder).For(moved).Should().Be(PluginIsolation.OutOfProcess);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
