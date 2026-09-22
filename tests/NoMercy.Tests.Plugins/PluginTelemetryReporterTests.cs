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

using System.Text.Json;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Telemetry;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Three rules and no fourth. Refusal counts always leave, counters leave only
/// on the owner's yes, and a sideloaded plugin is never mentioned at all.
/// </summary>
public class PluginTelemetryReporterTests
{
    private static readonly Ulid Marketplace = Ulid.Parse("01J9ZK5V8Y0000000000000009");
    private static readonly Ulid Sideloaded = Ulid.Parse("01J9ZK5V8Y000000000000000A");
    private static readonly Guid Server = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static (
        PluginTelemetryReporter Reporter,
        RecordingSink Sink,
        PluginCrashCounter Crashes
    ) Build(bool optedIn)
    {
        RecordingSink sink = new();
        PluginCrashCounter crashes = new();

        crashes.RecordCrash(Marketplace);
        crashes.RecordCrash(Marketplace);
        crashes.RecordCeilingHit(Marketplace);

        StubRefusals refusals = new();
        refusals.Add(Marketplace, PluginRefusalCodes.CapabilityNotDeclared, 3);

        return (
            new(
                new StubPlugins(Marketplace, Sideloaded),
                refusals,
                crashes,
                new StubFacts(Sideloaded),
                sink,
                () => new(optedIn),
                new MovableClock(Noon),
                Server
            ),
            sink,
            crashes
        );
    }

    [Fact]
    public void Refusal_counts_are_sent_without_any_opt_in()
    {
        (PluginTelemetryReporter reporter, _, _) = Build(optedIn: false);

        PluginTelemetryReport report = reporter.Build(Noon.AddHours(-1));

        report
            .Plugins.Should()
            .ContainSingle(entry => entry.PluginId == Marketplace)
            .Which.Refusals.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new PluginTelemetryRefusal(PluginRefusalCodes.CapabilityNotDeclared, 3)
            );
    }

    [Fact]
    public void Crash_and_ceiling_counters_need_the_opt_in()
    {
        (PluginTelemetryReporter reporter, _, _) = Build(optedIn: false);

        PluginTelemetryEntry entry = reporter.Build(Noon.AddHours(-1)).Plugins[0];

        entry.Crashes.Should().BeNull();
        entry.CeilingHits.Should().BeNull();
    }

    [Fact]
    public void With_the_opt_in_the_counters_are_included()
    {
        (PluginTelemetryReporter reporter, _, _) = Build(optedIn: true);

        PluginTelemetryEntry entry = reporter.Build(Noon.AddHours(-1)).Plugins[0];

        entry.Crashes.Should().Be(2);
        entry.CeilingHits.Should().Be(1);
    }

    [Fact]
    public void The_owner_taking_it_back_stops_the_counters_without_a_restart()
    {
        bool optedIn = true;
        PluginCrashCounter crashes = new();
        crashes.RecordCrash(Marketplace);

        PluginTelemetryReporter reporter = new(
            new StubPlugins(Marketplace, Sideloaded),
            new StubRefusals(),
            crashes,
            new StubFacts(Sideloaded),
            new RecordingSink(),
            () => new(optedIn),
            new MovableClock(Noon),
            Server
        );

        optedIn = false;

        reporter.Build(Noon).Plugins[0].Crashes.Should().BeNull();
    }

    [Fact]
    public void A_sideloaded_plugin_is_never_in_the_report()
    {
        (PluginTelemetryReporter reporter, _, _) = Build(optedIn: true);

        reporter
            .Build(Noon.AddHours(-1))
            .Plugins.Should()
            .NotContain(entry => entry.PluginId == Sideloaded);
    }

    [Fact]
    public void The_report_carries_no_person_and_nothing_they_watched()
    {
        (PluginTelemetryReporter reporter, _, _) = Build(optedIn: true);

        string json = JsonSerializer.Serialize(reporter.Build(Noon.AddHours(-1)));

        json.Should().NotContain("user");
        json.Should().NotContain("title");
        json.Should().NotContain("path");
    }

    [Fact]
    public async Task An_install_is_reported_for_a_marketplace_plugin_and_never_for_a_sideload()
    {
        (PluginTelemetryReporter reporter, RecordingSink sink, _) = Build(optedIn: false);

        await reporter.ReportInstallAsync(Marketplace, new(1, 0, 0), "install");
        await reporter.ReportInstallAsync(Sideloaded, new(1, 0, 0), "install");

        sink.Installs.Should().ContainSingle().Which.PluginId.Should().Be(Marketplace);
    }

    [Fact]
    public async Task A_window_that_was_sent_does_not_get_reported_again()
    {
        (PluginTelemetryReporter reporter, _, PluginCrashCounter crashes) = Build(optedIn: true);

        await reporter.SendAsync(Noon.AddHours(-1));

        crashes
            .CrashesFor(Marketplace)
            .Should()
            .Be(0, "keeping the count would report the same hour again next hour");
    }

    [Fact]
    public void The_window_sent_is_the_one_that_was_asked_for()
    {
        (PluginTelemetryReporter reporter, _, _) = Build(optedIn: false);

        PluginTelemetryReport report = reporter.Build(Noon.AddHours(-1));

        report.WindowStart.Should().Be(Noon.AddHours(-1));
        report.WindowEnd.Should().Be(Noon);
        report.ServerId.Should().Be(Server);
    }

    [Fact]
    public void An_unreadable_consent_file_is_read_as_no()
    {
        string folder = Path.Combine(Path.GetTempPath(), Ulid.NewUlid().ToString());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "telemetry.json"), "{ not json");

        try
        {
            PluginTelemetryOptions
                .Load(folder)
                .ShareCounters.Should()
                .BeFalse("a file nobody can read is not somebody agreeing");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void What_the_owner_answered_survives_being_written_down()
    {
        string folder = Path.Combine(Path.GetTempPath(), Ulid.NewUlid().ToString());

        try
        {
            new PluginTelemetryOptions(true).Save(folder);

            PluginTelemetryOptions.Load(folder).ShareCounters.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private sealed class StubPlugins(params Ulid[] ids) : IPluginManifestSource
    {
        public PluginInfo? Find(Ulid pluginId) => ids.Contains(pluginId) ? Info(pluginId) : null;

        public IReadOnlyList<PluginInfo> All() => [.. ids.Select(Info)];

        private static PluginInfo Info(Ulid id) =>
            new()
            {
                Id = id,
                Name = "Sample",
                Description = "d",
                Version = new(1, 0, 0),
                Status = PluginStatus.Active,
            };
    }

    private sealed class StubRefusals : IPluginRefusalCounter
    {
        private readonly Dictionary<Ulid, List<KeyValuePair<string, int>>> _counts = [];

        public void Add(Ulid pluginId, string code, int total)
        {
            if (!_counts.TryGetValue(pluginId, out List<KeyValuePair<string, int>>? held))
                _counts[pluginId] = held = [];

            held.Add(new(code, total));
        }

        public void Count(Ulid pluginId, PluginRefusal refusal) => Add(pluginId, refusal.Code, 1);

        public IReadOnlyList<KeyValuePair<string, int>> Counts(Ulid pluginId) =>
            _counts.TryGetValue(pluginId, out List<KeyValuePair<string, int>>? held) ? held : [];

        public void Clear(Ulid pluginId) => _counts.Remove(pluginId);
    }

    private sealed class StubFacts(Ulid sideloaded) : IPluginInstallFacts
    {
        public PluginTier TierOf(Ulid pluginId) => PluginTier.Free;

        public bool IsSideloaded(Ulid pluginId) => pluginId == sideloaded;

        public Guid? GuestFor(Ulid pluginId) => null;
    }

    private sealed class RecordingSink : IPluginTelemetrySink
    {
        public List<PluginTelemetryReport> Reports { get; } = [];
        public List<PluginInstallReport> Installs { get; } = [];

        public Task SendAsync(PluginTelemetryReport report, CancellationToken ct = default)
        {
            Reports.Add(report);

            return Task.CompletedTask;
        }

        public Task SendInstallAsync(PluginInstallReport report, CancellationToken ct = default)
        {
            Installs.Add(report);

            return Task.CompletedTask;
        }
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
