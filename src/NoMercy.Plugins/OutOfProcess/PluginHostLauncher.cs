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

using System.Diagnostics;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.Quotas;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>Where the host executable is, so a test can answer without one installed.</summary>
public interface IPluginHostExecutable
{
    string? Path { get; }
}

/// <summary>Beside the server's own binary, which is where the installer puts it.</summary>
public sealed class InstalledPluginHostExecutable : IPluginHostExecutable
{
    public string? Path
    {
        get
        {
            string beside = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                Software.IsWindows ? "NoMercy.PluginHost.exe" : "NoMercy.PluginHost"
            );

            return File.Exists(beside) ? beside : null;
        }
    }
}

/// <summary>
/// Starts one plugin in a process of its own, confined.
/// <para>
/// The endpoint comes up before the process does. Started the other way the
/// child dials a socket nobody is listening on, fails, and reports a plugin
/// that crashed rather than a server that was not ready.
/// </para>
/// </summary>
public sealed class PluginHostLauncher(
    IPluginBrokerFactory brokers,
    IPluginAssemblyLocation assemblies,
    IPluginQuotaSource quotas,
    IPluginHostExecutable executable,
    IPluginSandboxFactory sandboxes
) : IPluginProcessLauncher
{
    public async Task<IPluginHostProcess> LaunchAsync(Ulid pluginId, CancellationToken ct = default)
    {
        string? host = executable.Path;

        if (host is null)
            throw new PluginRefusedException(
                new PluginRefusal(
                    PluginRefusalCodes.HostServicesRemoved,
                    pluginId.ToString(),
                    "The owner asked for this plugin to run in a process of its own.",
                    "This install has no NoMercy.PluginHost beside the server, so there is nothing to start.",
                    "Run the plugin in the server's own process, or reinstall the server so the host ships with it. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
                    PluginRefusalSeverity.Blocked
                )
            );

        PluginFileLocation placement = assemblies.For(pluginId);
        PluginQuota quota = quotas.For(pluginId);

        PluginProcessLaunchPlan plan = PluginProcessLaunchPlan.For(
            pluginId,
            placement.AssemblyPath,
            placement.DataFolder,
            quota
        );

        PluginBrokerEndpoint endpoint = new(pluginId, plan.Token, brokers.For(pluginId));

        await endpoint.StartAsync(ct);

        PluginLocalTransport.ClearStaleEndpoint(PluginChannelEndpoints.HostFor(pluginId));

        IPluginSandbox sandbox = sandboxes.Create(
            new PluginSandboxGrants(placement.DataFolder, placement.AllowsSpawn)
        );

        PluginSandboxLaunch launch = sandbox.Wrap(new PluginSandboxLaunch(host, []));

        ProcessStartInfo start = new()
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = placement.DataFolder,
        };

        foreach (string argument in launch.Arguments)
            start.ArgumentList.Add(argument);

        foreach ((string key, string value) in plan.Environment)
            start.Environment[key] = value;

        Process process =
            Process.Start(start)
            ?? throw new PluginRefusedException(
                new PluginRefusal(
                    PluginRefusalCodes.HostServicesRemoved,
                    pluginId.ToString(),
                    "The owner asked for this plugin to run in a process of its own.",
                    "The operating system refused to start the plugin host.",
                    "Read the server log around this line, then start the plugin again from its health page. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
                    PluginRefusalSeverity.Blocked
                )
            );

        sandbox.Confine(process, quota);

        return new HostedPluginProcess(pluginId, process, endpoint, sandbox, plan.Token);
    }
}

/// <summary>
/// One running plugin, and everything that has to go away with it.
/// <para>
/// The endpoint closes after the process, not before. Closed first, a plugin
/// shutting down cleanly makes its last calls into a socket that is already
/// gone and reports refusals on its way out.
/// </para>
/// </summary>
internal sealed class HostedPluginProcess(
    Ulid pluginId,
    Process process,
    PluginBrokerEndpoint endpoint,
    IPluginSandbox sandbox,
    string token
) : IPluginHostProcess
{
    public Ulid PluginId => pluginId;

    public string Token => token;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone. Nothing to stop, and nothing worth saying.
        }
        finally
        {
            process.Dispose();
            sandbox.Dispose();
            await endpoint.DisposeAsync();
        }
    }
}
