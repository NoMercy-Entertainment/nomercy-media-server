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
using System.Runtime.Versioning;
using FluentAssertions;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.OutOfProcess;
using NoMercy.PluginSdk.Quotas;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// The real plugin host, confined the way the launcher confines it. The
/// kernel's task cap counts threads, and the .NET runtime runs more than the
/// sixteen the cap once held, so a host under that cap could not start.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginHostUnderCgroupTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("nm-cgroup-host-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException) { }
    }

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public async Task The_plugin_host_keeps_running_inside_its_slice()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Cgroups are a Linux facility.");

        using LinuxCgroupSandbox sandbox = new();
        Skip.IfNot(sandbox.Available, "This machine grants the server no cgroup of its own.");

        Ulid plugin = Ulid.NewUlid();
        string hostEndpoint = PluginChannelEndpoints.HostFor(plugin);
        PluginLocalTransport.ClearStaleEndpoint(hostEndpoint);

        ProcessStartInfo start = new()
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "NoMercy.PluginHost"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment[PluginChannelEnvironment.PluginId] = plugin.ToString();
        start.Environment[PluginChannelEnvironment.AssemblyPath] = Path.Combine(
            _data,
            "Missing.dll"
        );
        start.Environment[PluginChannelEnvironment.DataFolder] = _data;
        start.Environment[PluginChannelEnvironment.BrokerEndpoint] =
            PluginChannelEndpoints.BrokerFor(plugin);
        start.Environment[PluginChannelEnvironment.HostEndpoint] = hostEndpoint;
        start.Environment[PluginChannelEnvironment.Token] = "0123456789abcdef";

        using Process host = Process.Start(start)!;
        Task output = new PluginHostOutputPump(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
        ).DrainAsync(host.StandardOutput, host.StandardError);

        try
        {
            sandbox.Confine(host, new PluginQuota(0, 0, 0, 0)).Should().BeTrue();

            // Long enough for Kestrel and gRPC to come up and the thread pool
            // to start the threads it wants.
            await Task.Delay(TimeSpan.FromSeconds(3));

            host.HasExited.Should().BeFalse("a host under the task cap has to be able to start");

            int tasks = Directory.GetDirectories($"/proc/{host.Id}/task").Length;
            tasks
                .Should()
                .BeGreaterThan(
                    (int)PluginSandboxLimits.ProcessesPerPlugin,
                    "the runtime alone runs more threads than the process cap, which is why the cap must count tasks"
                );
            sandbox.ProcessLimitInKernel().Should().Be(PluginSandboxLimits.TasksPerPlugin);
        }
        finally
        {
            if (!host.HasExited)
                host.Kill(entireProcessTree: true);

            await output.WaitAsync(TimeSpan.FromSeconds(5));
            PluginLocalTransport.ClearStaleEndpoint(hostEndpoint);
        }
    }
}
