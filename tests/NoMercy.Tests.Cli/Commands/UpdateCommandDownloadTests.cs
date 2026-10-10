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
/// REQUIREMENT: <c>update</c> must stop at the download step — never proceed
/// to stop the running server — unless the server explicitly confirms the
/// download with <c>{"status":"ok"}</c>. A missing/unreachable response and a
/// non-"ok" status must both fail the command, and the message shown must
/// fall back to a fixed string only when the server didn't supply one. The
/// response DTO is a private nested type, so this runs against a real
/// <see cref="FakeManagementPipeServer"/> rather than a mock.
/// </summary>
[Trait("Category", "Unit")]
public sealed class UpdateCommandDownloadTests
{
    private static async Task<int> RunAsync(string pipeName)
    {
        Option<string?> pipeOption = new("--pipe", "-p");
        RootCommand root = new("test");
        root.Options.Add(pipeOption);
        root.Subcommands.Add(
            UpdateCommand.Create(
                pipeOption,
                new CliClientFactory(),
                startServer: _ => true,
                awaitVersion: (_, _) => Task.FromResult<string?>("9.9.9"),
                awaitExit: (_, _) => Task.FromResult(true)
            )
        );
        return await root.Parse(["--pipe", pipeName, "update"]).InvokeAsync();
    }

    [Fact]
    public async Task Download_ServerUnreachable_PrintsFallbackMessage_AndReturnsServerError()
    {
        FakeManagementPipeServer server = new();
        Task<string> serverTask = server.RunOnceAsync(stream =>
            FakeManagementPipeServer.WriteResponseAsync(stream, 500, "Internal Server Error", "")
        );

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(server.PipeName);

        await serverTask;
        exitCode.Should().Be((int)ExitCode.ServerError);
        console.Error.Should().Contain("Failed to download update.");
    }

    [Fact]
    public async Task Download_StatusNotOk_PrintsServerMessage_AndReturnsServerError_WithoutStopping()
    {
        FakeManagementPipeServer server = new();
        Task<string> serverTask = server.RunOnceAsync(stream =>
            FakeManagementPipeServer.WriteResponseAsync(
                stream,
                200,
                "OK",
                """{"status":"fail","message":"disk full"}"""
            )
        );

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(server.PipeName);

        string request = await serverTask;
        request.Should().StartWith("POST /manage/update");
        exitCode.Should().Be((int)ExitCode.ServerError);
        console.Error.Should().Contain("disk full");
    }

    [Fact]
    public async Task Download_Ok_PrintsMessage_AndProceedsToStop()
    {
        // The staged binary is now verified before the running server is stopped, so it has to
        // exist for the run to reach the stop step at all.
        Directory.CreateDirectory(AppFiles.BinariesPath);
        File.WriteAllText(AppFiles.ServerTempExePath, "NEW");

        FakeManagementPipeServer server = new();
        Task<List<string>> requestsTask = server.RunSequenceAsync([
            stream =>
                FakeManagementPipeServer.WriteResponseAsync(
                    stream,
                    200,
                    "OK",
                    """{"status":"ok","message":"Downloaded 120MB"}"""
                ),
            stream => FakeManagementPipeServer.WriteResponseAsync(stream, 200, "OK", "true"),
        ]);

        using ConsoleCapture console = new();
        // This test only asserts the download step's own console output and
        // that a second request (stop) really was sent — it deliberately
        // ignores the overall exit code, since the run continues past stop
        // into the wait-for-exit/file-swap steps (covered separately).
        _ = await RunAsync(server.PipeName);

        List<string> requests = await requestsTask;
        // Two, not three: the exit check is stubbed in this test, so the status poll the
        // real command would make never happens.
        requests.Should().HaveCount(2);
        requests[0].Should().StartWith("POST /manage/update");
        requests[1].Should().StartWith("POST /manage/stop");
        console.Out.Should().Contain("Downloading update...");
        console.Out.Should().Contain("Downloaded 120MB");
        console.Out.Should().Contain("Stopping server...");
    }

    [Theory]
    [InlineData("Server is already up to date.")]
    [InlineData("Binary on disk is already the latest version, restart needed to apply.")]
    public async Task Download_NoStagedUpdateNeeded_ExitsSuccessfullyWithoutStopping(string message)
    {
        FakeManagementPipeServer server = new();
        Task<string> requestTask = server.RunOnceAsync(stream =>
            FakeManagementPipeServer.WriteResponseAsync(
                stream,
                200,
                "OK",
                $"{{\"status\":\"ok\",\"message\":\"{message}\"}}"
            )
        );

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(server.PipeName);

        (await requestTask).Should().StartWith("POST /manage/update");
        exitCode.Should().Be((int)ExitCode.Success);
        console.Out.Should().Contain(message);
        console.Out.Should().NotContain("Stopping server...");
    }
}
