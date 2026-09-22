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

/// <summary>
/// Before an update or an uninstall the owner is really asking whether the
/// plugin's files can be changed. That is measured directly rather than
/// inferred from whether the load context unloaded — the proxy needs a forced
/// garbage collection to observe, and stopping every thread on a media server
/// is how playback stutters.
/// </summary>
public class PluginAssemblyTrackerTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.Parse("4SK6CSK6CSK6CSK6CSK6CSK6CS");

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "nomercy-tracker-" + Ulid.NewUlid().ToString()
    );

    private readonly string _assemblyPath;

    public PluginAssemblyTrackerTests()
    {
        Directory.CreateDirectory(_directory);
        _assemblyPath = Path.Combine(_directory, "Probe.dll");
        File.WriteAllText(_assemblyPath, "not really an assembly");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException) { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_plugin_that_was_never_unloaded_is_not_blocking_anything()
    {
        // Nothing was asked to unload, so nothing is in the way. Reporting
        // otherwise would make every fresh install claim it needs a restart.
        new PluginAssemblyTracker()
            .IsStillLoaded(PluginId)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void A_file_nothing_holds_is_replaceable()
    {
        PluginAssemblyTracker tracker = new();
        tracker.TrackUnload(PluginId, _assemblyPath);

        tracker.IsStillLoaded(PluginId).Should().BeFalse();
    }

    [Fact]
    public void A_file_something_still_has_open_is_reported_held()
    {
        // The case that matters, and the one a leftover cron executor or event
        // subscription produces: the process still has the assembly open, so
        // replacing it would fail.
        PluginAssemblyTracker tracker = new();
        tracker.TrackUnload(PluginId, _assemblyPath);

        using FileStream held = new(_assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        tracker.IsStillLoaded(PluginId).Should().BeTrue();
    }

    [Fact]
    public void A_file_already_gone_blocks_nothing()
    {
        PluginAssemblyTracker tracker = new();
        tracker.TrackUnload(PluginId, _assemblyPath);
        File.Delete(_assemblyPath);

        tracker.IsStillLoaded(PluginId).Should().BeFalse();
    }

    [Fact]
    public void A_path_that_was_never_recorded_blocks_nothing()
    {
        PluginAssemblyTracker tracker = new();
        tracker.TrackUnload(PluginId, null);

        tracker.IsStillLoaded(PluginId).Should().BeFalse();
    }

    [Fact]
    public void An_uninstall_whose_files_are_free_needs_no_restart()
    {
        PluginAssemblyTracker tracker = new();
        tracker.TrackUnload(PluginId, _assemblyPath);

        new PluginRestartAdvisor(tracker)
            .Evaluate(Plugin(), PluginOperation.Uninstall)
            .Required.Should()
            .BeFalse();
    }

    [Fact]
    public void An_uninstall_whose_files_are_held_says_so()
    {
        PluginAssemblyTracker tracker = new();
        tracker.TrackUnload(PluginId, _assemblyPath);

        using FileStream held = new(_assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        new PluginRestartAdvisor(tracker)
            .Evaluate(Plugin(), PluginOperation.Uninstall)
            .Reasons.Should()
            .HaveFlag(PluginRestartReason.AssemblyStillLoaded);
    }

    private static PluginInfo Plugin() =>
        new()
        {
            Id = PluginId,
            Name = "Probe",
            Description = "",
            Version = new(1, 0, 0),
            Status = PluginStatus.Deleted,
        };
}
