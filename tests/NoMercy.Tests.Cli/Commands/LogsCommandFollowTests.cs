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
using System.Text;
using Moq;
using NoMercy.Cli;
using NoMercy.Cli.Commands;
using NoMercy.Cli.Models;
using NoMercy.Tests.Cli.Support;
using NoMercy.Tests.Common.Ipc;
using Xunit;

namespace NoMercy.Tests.Cli.Commands;

/// <summary>
/// REQUIREMENT: <c>logs --follow</c> fetches the initial batch through the
/// injected <see cref="ICliClientFactory"/> (mockable), then reconnects over a
/// SECOND, real management-IPC connection to stream Server-Sent Events —
/// skipping blank/non-"data:" lines, tolerating a truncated/malformed JSON
/// event without dying, dropping entries that don't match <c>--level</c>/
/// <c>--type</c>, exiting 0 on an operator Ctrl+C (an <see cref="OperationCanceledException"/>),
/// and exiting with <see cref="ExitCode.ServerError"/> — not an unhandled
/// exception — when the stream connection itself fails.
///
/// The follow loop constructs its own <c>IpcClient</c> directly rather than
/// going through the factory, so these run against a real
/// <see cref="FakeManagementPipeServer"/> named pipe instead of a mock.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LogsCommandFollowTests
{
    public LogsCommandFollowTests()
    {
        PrivateReflection.ResetStaticField(
            typeof(LogsCommand),
            "_lastEntryTime",
            DateTime.MinValue
        );
    }

    private static Mock<ICliClientFactory> EmptyBatchFactory()
    {
        Mock<ICliClient> client = new();
        client
            .Setup(c =>
                c.GetAsync<List<LogEntryResponse>>(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);

        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);
        return factory;
    }

    private static async Task<int> RunAsync(
        ICliClientFactory factory,
        string pipeName,
        CancellationToken ct,
        params string[] extraArgs
    )
    {
        Option<string?> pipeOption = new("--pipe", "-p");
        RootCommand root = new("test");
        root.Options.Add(pipeOption);
        root.Subcommands.Add(LogsCommand.Create(pipeOption, factory));
        return await root.Parse(["--pipe", pipeName, "logs", "--follow", .. extraArgs])
            .InvokeAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task Follow_ParsesEventStream_SkippingNonDataAndMalformedLines_AndApplyingFilters()
    {
        FakeManagementPipeServer server = new();
        Task<string> serverTask = server.RunOnceAsync(async stream =>
        {
            await FakeManagementPipeServer.WriteResponseAsync(
                stream,
                200,
                "OK",
                body: string.Empty,
                contentType: "text/event-stream"
            );

            string[] lines =
            [
                """data: {"type":"App","message":"kept: info","color":"","threadId":1,"time":"2026-01-01T00:00:00Z","level":"Information"}""",
                "",
                ": a comment line, not data-prefixed",
                "",
                "data: not valid json {{{",
                "",
                """data: {"type":"App","message":"dropped: wrong level","color":"","threadId":1,"time":"2026-01-01T00:00:01Z","level":"Debug"}""",
                "",
                """data: {"type":"Other","message":"dropped: wrong type","color":"","threadId":1,"time":"2026-01-01T00:00:02Z","level":"Error"}""",
                "",
                "data: null",
                "",
                """data: {"type":"App","message":"kept: error","color":"","threadId":2,"time":"2026-01-01T00:00:03Z","level":"Error"}""",
                "",
            ];

            byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
            await stream.WriteAsync(bytes);
        });

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(
            EmptyBatchFactory().Object,
            server.PipeName,
            CancellationToken.None,
            "--level",
            "Information,Error",
            "--type",
            "App"
        );

        await serverTask;
        exitCode.Should().Be((int)ExitCode.Success);
        console.Out.Should().Contain("kept: info");
        console.Out.Should().Contain("kept: error");
        console.Out.Should().NotContain("dropped: wrong level");
        console.Out.Should().NotContain("dropped: wrong type");
    }

    [Fact]
    public async Task Follow_OperatorCancelsWhileStreaming_ReturnsSuccess()
    {
        FakeManagementPipeServer server = new();
        Task<string> serverTask = server.RunOnceAsync(async stream =>
        {
            await FakeManagementPipeServer.WriteResponseAsync(
                stream,
                200,
                "OK",
                body: string.Empty,
                contentType: "text/event-stream"
            );

            // Keep the connection open without sending a terminator so the
            // client is blocked in ReadLineAsync when the token cancels below.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        });

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(EmptyBatchFactory().Object, server.PipeName, cts.Token);

        await serverTask;
        exitCode.Should().Be((int)ExitCode.Success);
        console.Error.Should().NotContain("Stream disconnected");
    }

    [Fact]
    public async Task Follow_StreamConnectionFails_PrintsDisconnected_AndReturnsServerError()
    {
        string unusedPipeName = $"nomercy-test-no-listener-{Guid.NewGuid():N}";

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(
            EmptyBatchFactory().Object,
            unusedPipeName,
            CancellationToken.None
        );

        exitCode.Should().Be((int)ExitCode.ServerError);
        console.Error.Should().Contain("Stream disconnected");
    }
}
