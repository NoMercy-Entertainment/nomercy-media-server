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

using System.Text.Json.Nodes;
using FluentAssertions;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Hooks;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin that declares several cadences used to get one slot for all of
/// them, so the workaround was an internal scheduler in every such plugin and a
/// job list showing one opaque entry.
/// </summary>
public class PluginScheduledJobTests
{
    private sealed class MultiJobPlugin : IScheduledTaskPlugin
    {
        public string Name => "Multi";
        public string Description => "Several cadences";
        public Ulid Id { get; } = Ulid.Parse("248H248H248H248H248H248H24");
        public Version Version { get; } = new(1, 0, 0);
        public string CronExpression => "* * * * *";

        public List<string> Ran { get; } = [];

        public IReadOnlyList<PluginScheduledJob> Jobs =>
            [
                new() { Name = "fast", CronExpression = "* * * * *" },
                new() { Name = "slow", CronExpression = "0 * * * *" },
            ];

        public void Initialize(IPluginContext context) { }

        public Task ExecuteAsync(CancellationToken ct = default)
        {
            Ran.Add("(default)");
            return Task.CompletedTask;
        }

        public Task ExecuteAsync(string jobName, CancellationToken ct = default)
        {
            Ran.Add(jobName);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class SingleJobPlugin : IScheduledTaskPlugin
    {
        public string Name => "Single";
        public string Description => "One cadence";
        public Ulid Id { get; } = Ulid.Parse("2NANANANANANANANANANANANAN");
        public Version Version { get; } = new(1, 0, 0);
        public string CronExpression => "*/5 * * * *";
        public int Runs { get; private set; }

        public void Initialize(IPluginContext context) { }

        public Task ExecuteAsync(CancellationToken ct = default)
        {
            Runs++;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    [Fact]
    public void A_plugin_declaring_no_jobs_keeps_its_single_expression()
    {
        SingleJobPlugin plugin = new();
        PluginCronExecutor executor = new(plugin);

        executor.JobName.Should().Be($"plugin:{plugin.Id}");
        executor.CronExpression.Should().Be("*/5 * * * *");
    }

    [Fact]
    public void Each_declared_job_gets_its_own_name_and_cadence()
    {
        MultiJobPlugin plugin = new();

        PluginCronExecutor fast = new(plugin, plugin.Jobs[0]);
        PluginCronExecutor slow = new(plugin, plugin.Jobs[1]);

        fast.JobName.Should().Be($"plugin:{plugin.Id}:fast");
        fast.CronExpression.Should().Be("* * * * *");
        slow.JobName.Should().Be($"plugin:{plugin.Id}:slow");
        slow.CronExpression.Should().Be("0 * * * *");
    }

    [Fact]
    public async Task A_named_job_runs_by_its_name()
    {
        MultiJobPlugin plugin = new();
        PluginCronExecutor executor = new(plugin, plugin.Jobs[1]);

        await executor.ExecuteAsync(string.Empty);

        plugin.Ran.Should().BeEquivalentTo(["slow"]);
    }

    [Fact]
    public async Task A_tick_is_skipped_while_the_previous_one_is_still_running()
    {
        // An expensive cycle overrunning its interval must skip rather than
        // pile up, which is the thing a plugin could not express before.
        TaskCompletionSource release = new();
        BlockingPlugin plugin = new(release.Task);
        PluginCronExecutor executor = new(
            plugin,
            new() { Name = "slow", CronExpression = "* * * * *" }
        );

        Task first = executor.ExecuteAsync(string.Empty);
        await executor.ExecuteAsync(string.Empty);

        release.SetResult();
        await first;

        plugin.Started.Should().Be(1);
    }

    [Fact]
    public async Task A_job_that_asked_for_overlap_gets_it()
    {
        TaskCompletionSource release = new();
        BlockingPlugin plugin = new(release.Task);
        PluginCronExecutor executor = new(
            plugin,
            new()
            {
                Name = "parallel",
                CronExpression = "* * * * *",
                AllowConcurrent = true,
            }
        );

        Task first = executor.ExecuteAsync(string.Empty);
        Task second = executor.ExecuteAsync(string.Empty);

        release.SetResult();
        await Task.WhenAll(first, second);

        plugin.Started.Should().Be(2);
    }

    private sealed class BlockingPlugin(Task gate) : IScheduledTaskPlugin
    {
        public string Name => "Blocking";
        public string Description => "Holds until released";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0, 0);
        public string CronExpression => "* * * * *";
        public int Started;

        public void Initialize(IPluginContext context) { }

        public Task ExecuteAsync(CancellationToken ct = default) => ExecuteAsync("", ct);

        public async Task ExecuteAsync(string jobName, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Started);
            await gate;
        }

        public void Dispose() { }
    }
}

/// <summary>
/// Whether a capability set is baseline decides whether a plugin can enable
/// itself without the owner ever being asked, so the classification is a
/// security boundary rather than a categorisation.
/// </summary>
public class PluginElevatedCapabilityTests
{
    private static readonly PluginConsentService Service = new(new InMemoryConsentStore());

    [Fact]
    public void Library_write_is_never_baseline()
    {
        // The point of the constant. A hook that can delete a user's media must
        // not be able to arrive through an auto-enable.
        PluginCapabilities capabilities = new() { Hooks = [PluginHookCapability.LibraryWrite] };

        Service.IsBaseline(capabilities).Should().BeFalse();
    }

    [Fact]
    public void Library_write_stays_elevated_even_beside_baseline_hooks()
    {
        PluginCapabilities capabilities = new()
        {
            Hooks = [PluginHookCapability.Metadata, PluginHookCapability.LibraryWrite],
        };

        Service.IsBaseline(capabilities).Should().BeFalse();
    }

    [Fact]
    public void The_harmless_hooks_are_still_baseline()
    {
        PluginCapabilities capabilities = new()
        {
            Hooks = [PluginHookCapability.Metadata, PluginHookCapability.MediaSource],
        };

        Service.IsBaseline(capabilities).Should().BeTrue();
    }

    [Fact]
    public void Every_elevated_hook_is_named_rather_than_implied() =>
        PluginHookCapability
            .Elevated.Should()
            .Contain(PluginHookCapability.LibraryWrite)
            .And.NotContain(PluginHookCapability.Metadata);
}

/// <summary>
/// An unknown UI section used to be a silent no-op: the mount rendered nowhere
/// and nothing said so.
/// </summary>
public class PluginUiSectionTests
{
    [Theory]
    [InlineData(PluginUiSection.Music)]
    [InlineData(PluginUiSection.Library)]
    [InlineData(PluginUiSection.Dashboard)]
    public void A_known_section_is_kept(string section) =>
        PluginUiSection.OrFallback(section).Should().Be(section);

    [Theory]
    [InlineData("not-a-section")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_section_renders_somewhere_real(string? section) =>
        PluginUiSection.OrFallback(section).Should().Be(PluginUiSection.Addon);

    [Fact]
    public void Section_matching_ignores_case() =>
        PluginUiSection.OrFallback("MUSIC").Should().Be("MUSIC");
}

/// <summary>
/// A plugin's own event type lives in a collectible load context, so no host
/// subscriber can name it. The envelope is the type both sides share.
/// </summary>
public class PluginMessageEventTests
{
    private sealed record DownloadCompleted(string Path, int Episode);

    [Fact]
    public async Task A_host_subscriber_receives_what_a_plugin_published()
    {
        InMemoryEventBus bus = new();
        Ulid pluginId = Ulid.Parse("36CSK6CSK6CSK6CSK6CSK6CSK6");

        PluginMessageEvent? received = null;
        bus.Subscribe<PluginMessageEvent>(
            (@event, _) =>
            {
                received = @event;
                return Task.CompletedTask;
            }
        );

        PluginContext context = TestPluginPlatform.Context(
            bus,
            Path.GetTempPath(),
            TestStorageHelper.CreateStorage(Path.GetTempPath()),
            pluginId
        );

        await context.PublishAsync("download.completed", new DownloadCompleted("/x.mkv", 3));

        received.Should().NotBeNull();
        received!.PluginId.Should().Be(pluginId);
        received.Name.Should().Be("download.completed");
        received.PayloadAs<DownloadCompleted>()!.Episode.Should().Be(3);
    }

    [Fact]
    public void A_payload_of_the_wrong_shape_reads_null_rather_than_throwing()
    {
        // One plugin sending a malformed payload must not take down a host
        // subscriber that was expecting something else.
        PluginMessageEvent @event = new()
        {
            PluginId = Ulid.NewUlid(),
            Name = "x",
            Payload = JsonNode.Parse("\"just a string\""),
        };

        @event.PayloadAs<DownloadCompleted>().Should().BeNull();
    }

    [Fact]
    public void An_absent_payload_reads_null()
    {
        PluginMessageEvent @event = new() { PluginId = Ulid.NewUlid(), Name = "x" };

        @event.PayloadAs<DownloadCompleted>().Should().BeNull();
    }
}

/// <summary>
/// The shared-assembly set decides which types keep one identity across the
/// plugin boundary. It documented itself as bindable from configuration and
/// nothing bound it, so it was a hardcoded list.
/// </summary>
public class PluginHostOptionsTests
{
    [Fact]
    public void The_built_in_set_is_present_by_default() =>
        new PluginHostOptions()
            .SharedAssemblies.Should()
            .Contain("NoMercy.Plugins.Abstractions")
            .And.Contain("NoMercy.Events");

    [Fact]
    public void A_configured_assembly_is_added_to_the_built_in_set()
    {
        // Additive on purpose: a deployment adding one package must not have to
        // restate the six the platform requires, because forgetting one breaks
        // the boundary in a way that surfaces as a cast failure at load.
        PluginHostOptions options = new() { AdditionalSharedAssemblies = ["Contoso.Shared"] };

        options
            .SharedAssemblies.Should()
            .Contain("Contoso.Shared")
            .And.Contain("NoMercy.Plugins.Abstractions");
    }

    [Fact]
    public void An_explicit_set_replaces_the_default_entirely()
    {
        PluginHostOptions options = new()
        {
            SharedAssemblies = new HashSet<string> { "Only.This" },
        };

        options.SharedAssemblies.Should().BeEquivalentTo(["Only.This"]);
    }
}

/// <summary>
/// Hubs, action filters and the UI endpoints all need one plugin by id on a
/// request path. The accessors were added to <see cref="IPluginManager"/> with
/// defaults so that an existing implementer — including a third party's test
/// double — keeps compiling and still answers correctly.
/// </summary>
public class PluginManagerLookupTests
{
    private sealed class ListOnlyManager(params PluginInfo[] installed) : IPluginManager
    {
        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => installed;

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => [];
    }

    private static PluginInfo Plugin(Ulid id) =>
        new()
        {
            Id = id,
            Name = "n",
            Description = "d",
            Version = new(1, 0, 0),
            Status = PluginStatus.Active,
            Capabilities = new() { Rest = true },
        };

    [Fact]
    public void An_implementer_that_never_heard_of_the_accessor_still_answers()
    {
        Ulid id = Ulid.NewUlid();

        IPluginManager manager = new ListOnlyManager(Plugin(id));

        manager.GetPluginInfo(id)!.Capabilities!.Rest.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_id_is_null_rather_than_a_throw()
    {
        IPluginManager manager = new ListOnlyManager(Plugin(Ulid.NewUlid()));

        manager.GetPluginInfo(Ulid.NewUlid()).Should().BeNull();
        manager.GetPluginInstance(Ulid.NewUlid()).Should().BeNull();
    }
}
