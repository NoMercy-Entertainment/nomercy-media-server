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

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// One running plugin process, as the supervisor holds it.
/// <para>
/// Named apart from <c>IPluginProcess</c> in Abstractions, which is the facade
/// a plugin uses to start a program of its own. Both namespaces are imported
/// together all through this folder, and one name for two unrelated things is
/// a trap that only shows up as an ambiguous-reference error much later.
/// </para>
/// </summary>
public interface IPluginHostProcess
{
    Ulid PluginId { get; }

    bool IsRunning { get; }

    /// <summary>
    /// What this process was started with, so the server can call back into
    /// it. Minted per start, so a channel built with a stale one is refused by
    /// the plugin exactly as a stranger would be.
    /// </summary>
    string Token => string.Empty;

    Task StopAsync(CancellationToken ct = default);
}

/// <summary>Starts one plugin process. Replaced by the sandboxed launcher.</summary>
public interface IPluginProcessLauncher
{
    Task<IPluginHostProcess> LaunchAsync(Ulid pluginId, CancellationToken ct = default);
}

/// <summary>What the supervisor knows about a plugin right now.</summary>
public sealed record PluginProcessState(
    Ulid PluginId,
    bool Running,
    int CrashesInWindow,
    bool GaveUp
);

/// <summary>
/// Keeps each plugin's process alive, and keeps one plugin's failure to itself.
/// <para>
/// The whole point of moving a plugin out of the server process is that its
/// crash stops being the server's crash. That only holds if the supervisor
/// treats each plugin separately: a failure to start, a crash, or a plugin
/// that has crashed too often must change nothing about any other plugin.
/// </para>
/// <para>
/// A plugin that keeps crashing is given up on rather than restarted forever.
/// It stays given up until somebody asks for it explicitly, because a plugin
/// that crashes on its first line will do it again in a millisecond and the
/// server would spend the rest of its life spawning it.
/// </para>
/// </summary>
public sealed class PluginProcessSupervisor(
    IPluginProcessLauncher launcher,
    PluginRestartLedger? ledger = null,
    TimeProvider? time = null
)
{
    private readonly PluginRestartLedger _ledger = ledger ?? new PluginRestartLedger();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Ulid, IPluginHostProcess> _running = new();
    private readonly HashSet<Ulid> _gaveUp = [];
    private readonly Lock _gate = new();

    public event Action<Ulid, PluginRestartCause>? Exited;

    public async Task<bool> StartAsync(Ulid pluginId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // An explicit start is the way back from a give-up. Somebody asked
            // for this plugin, which is new information the ledger does not have.
            _gaveUp.Remove(pluginId);
            _ledger.Forget(pluginId);
        }

        return await LaunchAsync(pluginId, ct);
    }

    public async Task StopAsync(Ulid pluginId, CancellationToken ct = default)
    {
        IPluginHostProcess? process;

        lock (_gate)
        {
            _running.Remove(pluginId, out process);
            _gaveUp.Remove(pluginId);
        }

        if (process is not null)
            await process.StopAsync(ct);
    }

    /// <summary>
    /// The process ended. Whether it comes back is the ledger's decision, and
    /// either way no other plugin is touched.
    /// </summary>
    public async Task<bool> OnExitedAsync(
        Ulid pluginId,
        PluginRestartCause cause,
        CancellationToken ct = default
    )
    {
        lock (_gate)
            _running.Remove(pluginId);

        Exited?.Invoke(pluginId, cause);

        if (!_ledger.ShouldRestart(pluginId, cause, _time.GetUtcNow()))
        {
            lock (_gate)
                _gaveUp.Add(pluginId);

            return false;
        }

        return await LaunchAsync(pluginId, ct);
    }

    /// <summary>
    /// The launch token of the process running now, for the server's own
    /// calls back into it.
    /// <para>
    /// Deliberately not on <see cref="PluginProcessState" />: that record is
    /// what the health page renders, and a secret on it would be a secret on
    /// a screen.
    /// </para>
    /// </summary>
    internal string TokenFor(Ulid pluginId)
    {
        lock (_gate)
            return _running.TryGetValue(pluginId, out IPluginHostProcess? process)
                ? process.Token
                : string.Empty;
    }

    public PluginProcessState StateOf(Ulid pluginId)
    {
        lock (_gate)
            return new PluginProcessState(
                pluginId,
                _running.ContainsKey(pluginId),
                _ledger.CrashesInWindow(pluginId, _time.GetUtcNow()),
                _gaveUp.Contains(pluginId)
            );
    }

    public IReadOnlyCollection<Ulid> Running
    {
        get
        {
            lock (_gate)
                return [.. _running.Keys];
        }
    }

    /// <summary>
    /// A launch that throws is this plugin's failure and nobody else's, so the
    /// exception is swallowed here rather than travelling up into whatever
    /// loop is starting the other plugins.
    /// </summary>
    private async Task<bool> LaunchAsync(Ulid pluginId, CancellationToken ct)
    {
        try
        {
            IPluginHostProcess process = await launcher.LaunchAsync(pluginId, ct);

            lock (_gate)
                _running[pluginId] = process;

            return true;
        }
        catch (Exception)
        {
            lock (_gate)
                _gaveUp.Add(pluginId);

            return false;
        }
    }
}
