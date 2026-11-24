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
using Moq;
using NoMercy.Cli;
using NoMercy.Cli.Commands;
using NoMercy.Tests.Cli.Support;
using Xunit;

namespace NoMercy.Tests.Cli.Commands;

[Trait("Category", "Unit")]
public sealed class LogLevelCommandTests
{
    private static async Task<int> RunAsync(ICliClientFactory factory, params string[] args)
    {
        Option<string?> pipeOption = new("--pipe", "-p");
        RootCommand root = new("test");
        root.Options.Add(pipeOption);
        root.Subcommands.Add(LogLevelCommand.Create(pipeOption, factory));
        return await root.Parse(["loglevel", .. args]).InvokeAsync();
    }

    private static (Mock<ICliClient> Client, Mock<ICliClientFactory> Factory) Server(
        bool acknowledges
    )
    {
        Mock<ICliClient> client = new();
        client
            .Setup(c => c.PostAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(acknowledges);
        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);
        return (client, factory);
    }

    [Fact]
    public async Task LogLevel_PostsTheLevelToTheServer_AndReturnsSuccess()
    {
        (Mock<ICliClient> client, Mock<ICliClientFactory> factory) = Server(acknowledges: true);

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(factory.Object, "debug");

        exitCode.Should().Be((int)ExitCode.Success);
        client.Verify(c =>
            c.PostAsync(ApiRoutes.LogLevel + "?level=Debug", null, It.IsAny<CancellationToken>())
        );
        console.Out.Should().Contain("Log level set to Debug.");
    }

    [Fact]
    public async Task LogLevel_UnknownLevel_CallsNothing_AndFails()
    {
        (Mock<ICliClient> client, Mock<ICliClientFactory> factory) = Server(acknowledges: true);

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(factory.Object, "loud");

        exitCode.Should().NotBe((int)ExitCode.Success);
        client.Verify(
            c => c.PostAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task LogLevel_NotAcknowledged_ReturnsServerError()
    {
        (_, Mock<ICliClientFactory> factory) = Server(acknowledges: false);

        using ConsoleCapture console = new();
        int exitCode = await RunAsync(factory.Object, "error");

        exitCode.Should().Be((int)ExitCode.ServerError);
        console.Out.Should().NotContain("Log level set");
    }
}
