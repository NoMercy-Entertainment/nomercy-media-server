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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>
/// The metadata providers the owner already configured.
/// <para>
/// A plugin asks the server rather than carrying a key of its own, so the
/// provider the owner pays for is the one that answers, and a plugin the owner
/// removes stops querying with it.
/// </para>
/// </summary>
public sealed class RemoteMetadata(RemoteCall call) : IPluginMetadata
{
    public async Task<IReadOnlyList<PluginMetadataMatch>> QueryAsync(
        PluginMetadataQuery query,
        CancellationToken ct = default
    ) =>
        await call.AskAsync<IReadOnlyList<PluginMetadataMatch>>(
            "metadata",
            nameof(IPluginMetadata.QueryAsync),
            query
        ) ?? [];
}

/// <summary>
/// Notifications, which always name the person they are for.
/// <para>
/// The server is what knows how to reach somebody, on which device, in which
/// language. A plugin that pushed its own would be one more thing to silence
/// when the household wants quiet.
/// </para>
/// </summary>
public sealed class RemoteNotifications(RemoteCall call) : IPluginNotifications
{
    public Task PushAsync(
        UserId? user,
        PluginNotification notification,
        CancellationToken ct = default
    ) =>
        call.TellAsync(
            "notifications",
            nameof(IPluginNotifications.PushAsync),
            new { user = user?.Value.ToString(), notification }
        );
}

/// <summary>Who uses this server, as the owner allowed the plugin to know them.</summary>
public sealed class RemoteUsers(RemoteCall call) : IPluginUsers
{
    public async Task<IReadOnlyList<PluginUserIdentity>> ListAsync(
        CancellationToken ct = default
    ) =>
        await call.AskAsync<IReadOnlyList<PluginUserIdentity>>(
            "users",
            nameof(IPluginUsers.ListAsync)
        ) ?? [];
}

/// <summary>
/// Work the plugin asks the server to run later.
/// <para>
/// The queue stays the server's. A plugin holding its own timer would keep
/// running after the owner disabled it, and nothing on the jobs page would
/// show what was still going.
/// </para>
/// </summary>
public sealed class RemoteScheduler(Ulid pluginId, RemoteCall call) : IPluginScheduler
{
    public Task<JobId> RunNowAsync(string name, CancellationToken ct = default) =>
        Dispatch(nameof(IPluginScheduler.RunNowAsync), new { name });

    public Task<JobId> RunOnceAsync(
        string name,
        DateTimeOffset when,
        CancellationToken ct = default
    ) => Dispatch(nameof(IPluginScheduler.RunOnceAsync), new { name, when });

    public Task StopWorkerAsync(string name, CancellationToken ct = default) =>
        call.TellAsync("scheduler", nameof(IPluginScheduler.StopWorkerAsync), new { name });

    public async Task<PluginWorkerState> WorkerStateAsync(
        string name,
        CancellationToken ct = default
    ) =>
        await call.AskAsync<PluginWorkerState>(
            "scheduler",
            nameof(IPluginScheduler.WorkerStateAsync),
            new { name }
        );

    /// <summary>
    /// Registering a job and starting a worker hand the server a delegate, and
    /// a delegate does not cross a process boundary. They arrive with the
    /// hook-delivery task, which is what carries a call back the other way.
    /// </summary>
    public void Register(PluginScheduledJob job) =>
        throw new PluginRefusedException(NotYet(nameof(IPluginScheduler.Register)));

    public void StartWorker(PluginWorker worker) =>
        throw new PluginRefusedException(NotYet(nameof(IPluginScheduler.StartWorker)));

    private async Task<JobId> Dispatch(string member, object arguments) =>
        await call.AskAsync<JobId>("scheduler", member, arguments);

    private PluginRefusal NotYet(string member) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            pluginId.ToString(),
            $"A plugin in its own process asked for scheduler.{member}.",
            "That member hands the server a delegate, and a delegate cannot cross a process boundary.",
            "Declare the job in the manifest and implement IScheduledTaskPlugin instead. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            PluginRefusalSeverity.Blocked
        );
}
