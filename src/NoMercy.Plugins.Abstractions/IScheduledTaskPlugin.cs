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

using System.Diagnostics.CodeAnalysis;

namespace NoMercy.Plugins.Abstractions;

public interface IScheduledTaskPlugin : IPlugin
{
    string CronExpression { get; }
    Task ExecuteAsync(CancellationToken ct = default);

    /// <summary>
    /// Work that runs on its own cadence, when one expression per plugin is not
    /// enough.
    /// <para>
    /// A plugin with several kinds of periodic work at genuinely different
    /// cadences had one slot for all of them, so the workaround was to register
    /// the fastest one and gate the rest behind an internal scheduler. That
    /// costs every such plugin the same hand-rolled scheduler, and it shows the
    /// server's job list one opaque job in place of the real work.
    /// </para>
    /// <para>
    /// Defaulted to empty, so an existing plugin keeps working untouched. When
    /// it is empty the single <see cref="CronExpression"/> is registered as
    /// before; when it is not, each entry is registered separately and can be
    /// seen, timed and disabled on its own.
    /// </para>
    /// </summary>
    IReadOnlyList<PluginScheduledJob> Jobs => [];

    /// <summary>
    /// Runs one named job from <see cref="Jobs"/>. The default routes every
    /// name to <see cref="ExecuteAsync"/> so a plugin that declares jobs but
    /// has not split its entry point still behaves.
    /// </summary>
    Task ExecuteAsync(string jobName, CancellationToken ct = default) => ExecuteAsync(ct);
}

/// <summary>
/// One named job on a schedule.
/// <para>
/// Reached two ways, which is why the body is optional. A plugin registering
/// through <see cref="IPluginScheduler.Register" /> carries its own
/// <see cref="RunAsync" />; one declaring jobs on
/// <see cref="IScheduledTaskPlugin.Jobs" /> leaves it null, and its own
/// <see cref="IScheduledTaskPlugin.ExecuteAsync(string, CancellationToken)" />
/// is what runs. One type either way, so the job list shows the same thing.
/// </para>
/// </summary>
public sealed record PluginScheduledJob
{
    /// <summary>
    /// The shape this type had when it was positional.
    /// <para>
    /// A plugin pins the major, so everything built against 11.0 keeps calling
    /// <c>new PluginScheduledJob(name, cron, allowConcurrent)</c>. Dropping that
    /// constructor took both installed plugins off every client at once, with
    /// only a MissingMethodException in the log to say so.
    /// </para>
    /// </summary>
    [SetsRequiredMembers]
    public PluginScheduledJob(string name, string cronExpression, bool allowConcurrent = false)
    {
        Name = name;
        CronExpression = cronExpression;
        AllowConcurrent = allowConcurrent;
    }

    public PluginScheduledJob() { }

    /// <summary>Unique within the plugin. Becomes <c>plugin:{id}:{name}</c> in the job list.</summary>
    public required string Name { get; init; }

    /// <summary>Standard cron, same dialect as <see cref="IScheduledTaskPlugin.CronExpression"/>.</summary>
    public required string CronExpression { get; init; }

    /// <summary>
    /// Whether a tick may start while the previous one is still running. False
    /// by default: an expensive cycle overrunning its interval should skip, not
    /// pile up, and a plugin that wants overlap has to say so.
    /// </summary>
    public bool AllowConcurrent { get; init; }

    /// <summary>A key, not a sentence: the owner reads this in their language.</summary>
    public string? LabelKey { get; init; }

    /// <summary>Null when the plugin declared the job rather than registering it.</summary>
    public Func<IPluginContext, CancellationToken, Task>? RunAsync { get; init; }
}
