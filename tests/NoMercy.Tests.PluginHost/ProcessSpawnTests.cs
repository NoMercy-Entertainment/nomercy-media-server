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
using NoMercy.PluginHost;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;
using ProtoBuf.Grpc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// The plugin's half of <c>process.spawn</c>: it asks which file, and starts
/// it itself.
/// <para>
/// This is what puts the child in the sandbox. A process the server started
/// would be a child of the server, beside the job object or cgroup holding the
/// plugin; started here it is a child of the confined process and inherits all
/// of it, with nothing per-platform to get wrong.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ProcessSpawnTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    /// <summary>
    /// The plugin names a program and the server answers a file. Starting the
    /// name would be a PATH search, which is the thing the owner's approval
    /// list exists to remove.
    /// </summary>
    [Fact]
    public async Task ThePluginStartsTheFileTheServerNamed_NotTheNameItTyped()
    {
        RecordingStarter starter = new();
        RemoteProcess process = Build(Permitting("/opt/plugins/radio/ffmpeg"), starter);

        await process.SpawnAsync("ffmpeg", ["-version"]);

        starter.Request!.ResolvedPath.Should().Be("/opt/plugins/radio/ffmpeg");
        starter.Request.Arguments.Should().ContainSingle().Which.Should().Be("-version");
    }

    /// <summary>
    /// The start happens in this process, never on the server, because a
    /// process the server started would hold the server's rights.
    /// </summary>
    [Fact]
    public async Task TheServerIsAskedForAPermitAndNeverForAStart()
    {
        SpawnBroker broker = Permitting("/usr/bin/ffmpeg");
        RemoteProcess process = Build(broker, new RecordingStarter());

        await process.SpawnAsync("ffmpeg", []);

        broker.Facade.Should().Be("process");
        broker.Member.Should().Be(nameof(IPluginProcess.SpawnAsync));
    }

    /// <summary>
    /// A credential belongs in the environment. An argument is visible to
    /// every other user on the machine through the process list, so a key
    /// passed as one leaks locally whatever the plugin does afterwards.
    /// </summary>
    [Fact]
    public async Task TheEnvironmentReachesTheChildSeparatelyFromTheArguments()
    {
        RecordingStarter starter = new();
        RemoteProcess process = Build(Permitting("/usr/bin/ffmpeg"), starter);

        await process.SpawnAsync(
            "ffmpeg",
            ["-i", "in.mkv"],
            new Dictionary<string, string> { ["API_KEY"] = "a-secret" }
        );

        starter.Request!.Environment!["API_KEY"].Should().Be("a-secret");
        starter.Request.Arguments.Should().NotContain("a-secret");
    }

    /// <summary>
    /// The server's refusal crosses back whole. A spawn that failed silently
    /// reads to a plugin author as a binary that is missing rather than a
    /// capability the owner never granted.
    /// </summary>
    [Fact]
    public async Task ARefusalFromTheServer_CrossesBackWithItsOwnCode()
    {
        RecordingStarter starter = new();
        RemoteProcess process = Build(Refusing(), starter);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            process.SpawnAsync("curl", [])
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ProcessSpawnUndeclared);
        starter.Request.Should().BeNull();
    }

    /// <summary>
    /// The owner granted the binary and the start still failed, so what
    /// refused is the confinement rather than the permission, and the owner
    /// is told that rather than being sent to edit a manifest that is right.
    /// </summary>
    [Fact]
    public async Task AStartTheSandboxRefuses_SaysSoRatherThanBlamingTheManifest()
    {
        RemoteProcess process = Build(Permitting("/usr/bin/ffmpeg"), new ThrowingStarter());

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            process.SpawnAsync("ffmpeg", [])
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ProcessSpawnOutsideSandbox);
    }

    /// <summary>
    /// The health page shows what a plugin is running now. A child that had
    /// already exited would read as work still in flight.
    /// </summary>
    [Fact]
    public async Task RunningListsTheLiveChildrenAndDropsTheExitedOnes()
    {
        RecordingStarter starter = new();
        RemoteProcess process = Build(Permitting("/usr/bin/ffmpeg"), starter);

        await process.SpawnAsync("ffmpeg", []);

        process.Running.Should().ContainSingle();

        starter.Handle!.Exit();

        process.Running.Should().BeEmpty();
    }

    private static RemoteProcess Build(IPluginBrokerService broker, ILocalProcessStarter starter) =>
        new(
            PluginId,
            new RemoteCall(PluginId, broker),
            new PluginHostLaunch(PluginId, "plugin.dll", "/data", "broker", "host", "token"),
            starter
        );

    private static SpawnBroker Permitting(string path) =>
        new(
            PluginCallResponse.Value(
                JsonSerializer.Serialize(
                    new PluginSpawnPermit(path),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )
            )
        );

    private static SpawnBroker Refusing() =>
        new(
            PluginCallResponse.Refused(
                new WireRefusal(
                    PluginRefusalCodes.ProcessSpawnUndeclared,
                    PluginId.ToString(),
                    "The plugin started a child process: curl",
                    "The owner approved no binary by that name.",
                    "Declare process.spawn naming curl. Docs: /nomercy-plugins/capabilities/process-spawn",
                    PluginRefusalSeverity.Blocked.ToString()
                )
            )
        );
}

internal sealed class SpawnBroker(PluginCallResponse response) : IPluginBrokerService
{
    public string? Facade { get; private set; }

    public string? Member { get; private set; }

    public Task<PluginCallResponse> CallAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        Facade = request.Facade;
        Member = request.Member;

        return Task.FromResult(response);
    }

    public Task<PluginCallResponse> PublishAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => CallAsync(request, context);

    public async IAsyncEnumerable<PluginCallRequest> SubscribeAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        await Task.CompletedTask;
        yield break;
    }
}

file sealed class RecordingStarter : ILocalProcessStarter
{
    public LocalProcessRequest? Request { get; private set; }

    public FakeHandle? Handle { get; private set; }

    public IPluginProcessHandle Start(LocalProcessRequest request)
    {
        Request = request;
        Handle = new FakeHandle();

        return Handle;
    }
}

file sealed class ThrowingStarter : ILocalProcessStarter
{
    public IPluginProcessHandle Start(LocalProcessRequest request) =>
        throw new InvalidOperationException("The sandbox refused the start.");
}

file sealed class FakeHandle : IPluginProcessHandle
{
    public int Id => 4242;

    public bool HasExited { get; private set; }

    public int ExitCode => 0;

    public TextReader StandardOutput { get; } = TextReader.Null;

    public TextReader StandardError { get; } = TextReader.Null;

    public void Exit() => HasExited = true;

    public Task<int> WaitForExitAsync(CancellationToken ct = default) => Task.FromResult(0);

    public void Kill() => HasExited = true;

    public ValueTask DisposeAsync()
    {
        HasExited = true;

        return ValueTask.CompletedTask;
    }
}
