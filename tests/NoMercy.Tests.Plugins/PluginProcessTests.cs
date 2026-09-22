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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Runtime;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A child process runs as the server's own user with the server's own
/// rights. So it is the file the owner read on the permissions page, never
/// the first hit on PATH, and it stops when the plugin does.
/// </summary>
public class PluginProcessTests : IDisposable
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000016");

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nm-plugin-process-{Ulid.NewUlid()}"
    );

    public PluginProcessTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);

        GC.SuppressFinalize(this);
    }

    /// <summary>A real file, because an approved binary that is not there is not approved.</summary>
    private string Binary(string name)
    {
        string path = Path.Combine(_folder, OperatingSystem.IsWindows() ? $"{name}.exe" : name);
        File.WriteAllText(path, "not really a program");

        return path;
    }

    private (PluginProcess Process, RecordingStarter Starter, PluginResourceLedger Ledger) Process(
        bool declared,
        params string[] approvedPaths
    )
    {
        PluginCapabilities? capabilities = declared
            ? new() { Hooks = [PluginCapabilityNames.ProcessSpawn] }
            : null;

        GrantingPlugin plugin = new(
            Plugin,
            capabilities,
            PluginGrantKind.ForCapability(PluginCapabilityNames.ProcessSpawn),
            approvedPaths
        );
        PluginCapabilityBroker broker = new(
            plugin,
            plugin,
            plugin,
            new PluginRefusalCounter(),
            NullLogger<PluginCapabilityBroker>.Instance
        );
        RecordingStarter starter = new();
        PluginResourceLedger ledger = new();

        return (
            new(Plugin, broker, new PluginApprovedBinaries(plugin), starter, ledger),
            starter,
            ledger
        );
    }

    [Fact]
    public async Task Spawning_without_the_capability_refuses_and_names_the_binary()
    {
        string ffmpeg = Binary("ffmpeg");
        (PluginProcess process, RecordingStarter starter, _) = Process(declared: false, ffmpeg);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            process.SpawnAsync("ffmpeg", ["-version"])
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ProcessSpawnUndeclared);
        refused.Refusal.What.Should().Contain("ffmpeg");
        starter.Started.Should().BeEmpty("nothing runs before the owner has agreed");
    }

    [Fact]
    public async Task A_binary_the_owner_never_approved_refuses_even_with_the_capability()
    {
        (PluginProcess process, RecordingStarter starter, _) = Process(declared: true);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            process.SpawnAsync("ffmpeg", ["-version"])
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.ProcessSpawnUndeclared);
        starter.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task The_binary_that_runs_is_the_absolute_file_the_owner_approved()
    {
        string ffmpeg = Binary("ffmpeg");
        (PluginProcess process, RecordingStarter starter, _) = Process(declared: true, ffmpeg);

        await process.SpawnAsync("ffmpeg", ["-version"]);

        starter
            .Started.Should()
            .ContainSingle()
            .Which.Binary.Should()
            .Be(ffmpeg, "a bare name would be resolved through PATH by the operating system");
    }

    [Fact]
    public async Task A_name_that_is_a_path_to_something_else_is_not_approved()
    {
        string ffmpeg = Binary("ffmpeg");
        (PluginProcess process, RecordingStarter starter, _) = Process(declared: true, ffmpeg);

        await Assert.ThrowsAsync<PluginRefusedException>(() =>
            process.SpawnAsync(
                OperatingSystem.IsWindows() ? "C:/Windows/System32/cmd.exe" : "/bin/sh",
                []
            )
        );

        starter.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Arguments_are_passed_through_as_they_were_written()
    {
        string ffmpeg = Binary("ffmpeg");
        (PluginProcess process, RecordingStarter starter, _) = Process(declared: true, ffmpeg);

        await process.SpawnAsync("ffmpeg", ["-i", "a file with spaces.mkv", "-c", "copy"]);

        starter
            .Started[0]
            .Arguments.Should()
            .Equal(
                ["-i", "a file with spaces.mkv", "-c", "copy"],
                "one argument per entry, so a path with a space is not two arguments"
            );
    }

    [Fact]
    public async Task A_credential_goes_in_the_environment_and_never_in_an_argument()
    {
        string ffmpeg = Binary("ffmpeg");
        (PluginProcess process, RecordingStarter starter, _) = Process(declared: true, ffmpeg);

        await process.SpawnAsync(
            "ffmpeg",
            ["-version"],
            new Dictionary<string, string> { ["API_KEY"] = "secret" }
        );

        PluginProcessRequest request = starter.Started[0];

        request.Environment!["API_KEY"].Should().Be("secret");
        request
            .Arguments.Should()
            .NotContain(
                "secret",
                "an argument is readable by every other local user through the process list"
            );
    }

    [Fact]
    public async Task Running_lists_the_children_and_drops_the_ones_that_have_gone()
    {
        string ffmpeg = Binary("ffmpeg");
        (PluginProcess process, RecordingStarter starter, _) = Process(declared: true, ffmpeg);

        IPluginProcessHandle first = await process.SpawnAsync("ffmpeg", ["one"]);
        await process.SpawnAsync("ffmpeg", ["two"]);

        process.Running.Should().HaveCount(2);
        process.Running.Select(handle => handle.Id).Should().OnlyHaveUniqueItems();

        ((RecordingHandle)first).Exited = true;

        process.Running.Should().ContainSingle("a child that has gone is not running");
        starter.Started.Should().HaveCount(2);
    }

    [Fact]
    public async Task Stopping_the_plugin_kills_every_child_it_started()
    {
        string ffmpeg = Binary("ffmpeg");
        (PluginProcess process, RecordingStarter starter, PluginResourceLedger ledger) = Process(
            declared: true,
            ffmpeg
        );
        await process.SpawnAsync("ffmpeg", ["one"]);
        await process.SpawnAsync("ffmpeg", ["two"]);

        await ledger.ReleaseAsync(Plugin);

        starter
            .Handles.Should()
            .OnlyContain(handle => handle.Killed, "a child nobody is left to stop keeps running");
    }

    private sealed class RecordingStarter : IPluginProcessStarter
    {
        private int _next = 1000;

        public List<PluginProcessRequest> Started { get; } = [];
        public List<RecordingHandle> Handles { get; } = [];

        public IPluginProcessHandle Start(PluginProcessRequest request)
        {
            Started.Add(request);

            RecordingHandle handle = new(_next++);
            Handles.Add(handle);

            return handle;
        }
    }

    private sealed class RecordingHandle(int id) : IPluginProcessHandle
    {
        public int Id { get; } = id;

        public bool Exited { get; set; }

        public bool Killed { get; private set; }

        public bool HasExited => Exited;

        public int ExitCode => 0;

        public TextReader StandardOutput { get; } = TextReader.Null;

        public TextReader StandardError { get; } = TextReader.Null;

        public Task<int> WaitForExitAsync(CancellationToken ct = default) => Task.FromResult(0);

        public void Kill()
        {
            Killed = true;
            Exited = true;
        }

        public ValueTask DisposeAsync()
        {
            Kill();

            return ValueTask.CompletedTask;
        }
    }
}
