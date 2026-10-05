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

using System.CommandLine;
using NoMercy.Cli;
using NoMercy.Cli.Commands;
using NoMercy.NmSystem.Information;
using NoMercy.Tests.Cli.Support;
using NoMercy.Tests.Common.Ipc;
using Xunit;

namespace NoMercy.Tests.Cli.Commands;

/// <summary>
/// REQUIREMENT: `nomercy update` must never leave a user without a working server. Every one
/// of these injects a failure at a different point in the sequence and asserts the outcome on
/// disk, because the failure modes that matter are all "what is left behind when this goes
/// wrong halfway" — not what the command printed.
///
/// The invariant under test: whatever happens, the executable at ServerExePath is either the
/// new version or the one the machine started with. Never absent, never truncated.
/// </summary>
[Trait("Category", "Unit")]
public sealed class UpdateCommandFailureInjectionTests : IDisposable
{
    private readonly string _tempExePath = AppFiles.ServerTempExePath;
    private readonly string _currentExePath = AppFiles.ServerExePath;
    private readonly string _backupPath = AppFiles.ServerExePath + ".previous";

    public UpdateCommandFailureInjectionTests()
    {
        Directory.CreateDirectory(AppFiles.BinariesPath);
        Cleanup();
    }

    public void Dispose()
    {
        Cleanup();
    }

    private void Cleanup()
    {
        foreach (string path in new[] { _tempExePath, _currentExePath, _backupPath })
            if (File.Exists(path))
                File.Delete(path);
    }

    private const string DownloadOk = """{"status":"ok","message":"Downloaded"}""";

    /// <summary>
    /// Drives the real command. <paramref name="stopAcknowledged"/> false makes the server
    /// refuse to stop; the third responder drops the connection so the exit poll resolves
    /// immediately instead of burning the real timeout.
    /// </summary>
    private static async Task<int> RunAsync(
        string downloadBody = DownloadOk,
        bool stopAcknowledged = true,
        bool serverStarts = true,
        string? versionAfterStart = "9.9.9"
    )
    {
        FakeManagementPipeServer server = new();
        Task<List<string>> requestsTask = server.RunSequenceAsync([
            stream => FakeManagementPipeServer.WriteResponseAsync(stream, 200, "OK", downloadBody),
            stream =>
                stopAcknowledged
                    ? FakeManagementPipeServer.WriteResponseAsync(stream, 200, "OK", "true")
                    : FakeManagementPipeServer.WriteResponseAsync(
                        stream,
                        500,
                        "Internal Server Error",
                        ""
                    ),
            _ => Task.CompletedTask,
        ]);

        Option<string?> pipeOption = new("--pipe", "-p");
        RootCommand root = new("test");
        root.Options.Add(pipeOption);
        root.Subcommands.Add(
            UpdateCommand.Create(
                pipeOption,
                new CliClientFactory(),
                startServer: _ => serverStarts,
                awaitVersion: (_, _) => Task.FromResult(versionAfterStart),
                awaitExit: (_, _) => Task.FromResult(true)
            )
        );

        int exitCode = await root.Parse(["--pipe", server.PipeName, "update"]).InvokeAsync();

        // Deliberately not awaited. Several of these scenarios return early by design — a
        // missing staged file or a container deployment never sends a stop — so the queued
        // responders go unused, and waiting on them would just burn the fake server's accept
        // timeout. What these tests assert is the state left on disk.
        _ = requestsTask;
        return exitCode;
    }

    [Fact]
    public async Task StopRefused_LeavesBothBinariesExactlyAsTheyWere()
    {
        File.WriteAllText(_currentExePath, "OLD");
        File.WriteAllText(_tempExePath, "NEW");

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(stopAcknowledged: false);

        exitCode.Should().NotBe(0);
        File.ReadAllText(_currentExePath).Should().Be("OLD");
        File.Exists(_tempExePath).Should().BeTrue("a refused stop must not consume the download");
    }

    [Fact]
    public async Task UpdatedBinaryWillNotStart_RollsBackToTheWorkingVersion()
    {
        File.WriteAllText(_currentExePath, "OLD");
        File.WriteAllText(_tempExePath, "NEW");

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(serverStarts: false);

        exitCode.Should().NotBe(0);
        File.Exists(_currentExePath).Should().BeTrue();
        File.ReadAllText(_currentExePath)
            .Should()
            .Be("OLD", "a version that cannot start is worse than the one that could");
        File.Exists(_backupPath).Should().BeFalse("the backup was consumed by the restore");
    }

    [Fact]
    public async Task UpdatedBinaryNeverReportsAVersion_RollsBack()
    {
        File.WriteAllText(_currentExePath, "OLD");
        File.WriteAllText(_tempExePath, "NEW");

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(versionAfterStart: null);

        exitCode.Should().NotBe(0);

        // Starting is not the same as working. A process that launches and then dies still
        // leaves the user without a server, so it has to roll back too.
        File.ReadAllText(_currentExePath).Should().Be("OLD");
    }

    [Fact]
    public async Task SuccessfulUpdate_LeavesTheNewBinaryAndNoBackup()
    {
        File.WriteAllText(_currentExePath, "OLD");
        File.WriteAllText(_tempExePath, "NEW");

        using ConsoleCapture console = new();
        int exitCode = await RunAsync();

        exitCode.Should().Be(0);
        File.ReadAllText(_currentExePath).Should().Be("NEW");
        File.Exists(_tempExePath).Should().BeFalse("a consumed staging file must not linger");
        File.Exists(_backupPath).Should().BeFalse();
        console.Out.Should().Contain("9.9.9", "the command must report the version that came up");
    }

    [Fact]
    public async Task ContainerDeployment_TouchesNothingOnDisk()
    {
        File.WriteAllText(_currentExePath, "OLD");

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(
            downloadBody: """
            {"status":"ok","message":"Pull the image","use_container_image":true}
            """
        );

        // In a container the swap cannot work and stopping the server ends PID 1, so the only
        // safe outcome is to change nothing and say so.
        exitCode.Should().Be(0);
        File.ReadAllText(_currentExePath).Should().Be("OLD");
        File.Exists(_backupPath).Should().BeFalse();
        console.Out.Should().Contain("container image");
    }

    [Fact]
    public async Task InstallerDeployment_TouchesNothingOnDisk()
    {
        File.WriteAllText(_currentExePath, "OLD");

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(
            downloadBody: """
            {"status":"ok","message":"Use the installer","use_installer":true}
            """
        );

        exitCode.Should().Be(0);
        File.ReadAllText(_currentExePath).Should().Be("OLD");
        console.Out.Should().Contain("installer");
    }

    [Fact]
    public async Task NothingStaged_NeverStopsTheRunningServer()
    {
        File.WriteAllText(_currentExePath, "OLD");

        using ConsoleCapture console = new();
        int exitCode = await RunAsync();

        exitCode.Should().NotBe(0);
        File.ReadAllText(_currentExePath).Should().Be("OLD");
    }
}
