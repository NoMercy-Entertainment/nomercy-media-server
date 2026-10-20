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
using NoMercy.Cli.Models;
using NoMercy.Tests.Cli.Support;
using Xunit;

namespace NoMercy.Tests.Cli.Commands;

/// <summary>
/// REQUIREMENT: <c>config get</c> must fail distinctly when unreachable and
/// otherwise print every field the server reports. <c>config set &lt;key&gt;
/// &lt;value&gt;</c> must translate the key to snake_case and the value to the
/// type the server declares for that key before sending it — getting either
/// translation wrong silently corrupts the value the server receives. A key
/// the server does not read on PUT must be refused before anything is sent:
/// the server answers 200 to a body it does not understand, so sending it
/// would report success for a change that never happened.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConfigCommandTests
{
    private static async Task<int> RunGetAsync(ICliClientFactory factory)
    {
        Option<string?> pipeOption = new("--pipe", "-p");
        RootCommand root = new("test");
        root.Options.Add(pipeOption);
        root.Subcommands.Add(ConfigCommand.Create(pipeOption, factory));
        return await root.Parse(["config", "get"]).InvokeAsync();
    }

    private static async Task<int> RunSetAsync(ICliClientFactory factory, string key, string value)
    {
        Option<string?> pipeOption = new("--pipe", "-p");
        RootCommand root = new("test");
        root.Options.Add(pipeOption);
        root.Subcommands.Add(ConfigCommand.Create(pipeOption, factory));
        return await root.Parse(["config", "set", key, value]).InvokeAsync();
    }

    [Fact]
    public async Task Get_ServerUnreachable_PrintsError_AndReturnsServerError()
    {
        Mock<ICliClient> client = new();
        client
            .Setup(c => c.GetAsync<ConfigResponse>(ApiRoutes.Config, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConfigResponse?)null);

        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);

        using ConsoleCapture console = new();
        int exitCode = await RunGetAsync(factory.Object);

        exitCode.Should().Be((int)ExitCode.ServerError);
        console.Error.Should().Contain("Could not connect to server.");
    }

    [Fact]
    public async Task Get_ServerReachable_PrintsEveryField()
    {
        ConfigResponse config = new()
        {
            ServerName = "nomercy-test",
            InternalPort = 7626,
            ExternalPort = 7627,
            LibraryWorkers = 2,
            ImportWorkers = 3,
            ExtrasWorkers = 5,
            EncoderWorkers = 1,
            CronWorkers = 1,
            ImageWorkers = 10,
            FileWorkers = 4,
            MusicWorkers = 6,
            Swagger = true,
        };

        Mock<ICliClient> client = new();
        client
            .Setup(c => c.GetAsync<ConfigResponse>(ApiRoutes.Config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);

        using ConsoleCapture console = new();
        int exitCode = await RunGetAsync(factory.Object);

        exitCode.Should().Be((int)ExitCode.Success);
        console.Out.Should().Contain("Server Name:      nomercy-test");
        console.Out.Should().Contain("Internal Port:    7626");
        console.Out.Should().Contain("External Port:    7627");
        console.Out.Should().Contain("Library Workers:  2");
        console.Out.Should().Contain("Import Workers:   3");
        console.Out.Should().Contain("Extras Workers:   5");
        console.Out.Should().Contain("Encoder Workers:  1");
        console.Out.Should().Contain("Cron Workers:     1");
        console.Out.Should().Contain("Image Workers:    10");
        console.Out.Should().Contain("File Workers:     4");
        console.Out.Should().Contain("Music Workers:    6");
        console.Out.Should().Contain("Swagger:          True");
    }

    [Theory]
    [InlineData(["serverName", "server_name", "MyServer"])]
    [InlineData(["musicWorkers", "music_workers", "4"])]
    public async Task Set_TranslatesKeyToSnakeCase_InPayload(
        string key,
        string expectedKey,
        string value
    )
    {
        string? capturedJson = null;
        Mock<ICliClient> client = new();
        client
            .Setup(c =>
                c.PutAsync(ApiRoutes.Config, It.IsAny<HttpContent>(), It.IsAny<CancellationToken>())
            )
            .Callback<string, HttpContent?, CancellationToken>(
                (_, content, _) => capturedJson = content!.ReadAsStringAsync().Result
            )
            .ReturnsAsync(true);

        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);

        using ConsoleCapture console = new();
        int exitCode = await RunSetAsync(factory.Object, key, value);

        exitCode.Should().Be((int)ExitCode.Success);
        capturedJson.Should().Contain($"\"{expectedKey}\"");
        console.Out.Should().Contain($"Configuration updated: {key} = {value}");
    }

    [Theory]
    [InlineData(["42", "42"])]
    [InlineData(["-7", "-7"])]
    public async Task Set_IntegerValue_SerializesAsJsonNumber(
        string value,
        string expectedJsonValue
    )
    {
        string? capturedJson = null;
        Mock<ICliClient> client = new();
        client
            .Setup(c =>
                c.PutAsync(ApiRoutes.Config, It.IsAny<HttpContent>(), It.IsAny<CancellationToken>())
            )
            .Callback<string, HttpContent?, CancellationToken>(
                (_, content, _) => capturedJson = content!.ReadAsStringAsync().Result
            )
            .ReturnsAsync(true);

        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);

        using ConsoleCapture _ = new();
        await RunSetAsync(factory.Object, "encoder_workers", value);

        capturedJson.Should().Be($"{{\"encoder_workers\":{expectedJsonValue}}}");
    }

    [Fact]
    public async Task Set_TextValue_SerializesAsJsonString()
    {
        string? capturedJson = null;
        Mock<ICliClient> client = new();
        client
            .Setup(c =>
                c.PutAsync(ApiRoutes.Config, It.IsAny<HttpContent>(), It.IsAny<CancellationToken>())
            )
            .Callback<string, HttpContent?, CancellationToken>(
                (_, content, _) => capturedJson = content!.ReadAsStringAsync().Result
            )
            .ReturnsAsync(true);

        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);

        using ConsoleCapture _ = new();
        await RunSetAsync(factory.Object, "server_name", "MyServer");

        capturedJson.Should().Be("""{"server_name":"MyServer"}""");
    }

    [Fact]
    public async Task Set_NotAcknowledged_ReturnsServerError_WithoutSuccessMessage()
    {
        Mock<ICliClient> client = new();
        client
            .Setup(c =>
                c.PutAsync(ApiRoutes.Config, It.IsAny<HttpContent>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(false);

        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);

        using ConsoleCapture console = new();
        int exitCode = await RunSetAsync(factory.Object, "server_name", "MyServer");

        exitCode.Should().Be((int)ExitCode.ServerError);
        console.Out.Should().NotContain("updated");
    }

    [Theory]
    [InlineData("queue_workers")]
    [InlineData("internal_port")]
    [InlineData("swagger")]
    public async Task Set_KeyTheServerDoesNotWrite_IsRefused_WithoutCallingClient(string key)
    {
        Mock<ICliClient> client = new();
        client
            .Setup(c =>
                c.PutAsync(ApiRoutes.Config, It.IsAny<HttpContent>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(true);

        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);

        using ConsoleCapture console = new();
        int exitCode = await RunSetAsync(factory.Object, key, "4");

        exitCode.Should().Be((int)ExitCode.ConfigurationError);
        console.Error.Should().Contain(key);
        console.Error.Should().Contain("library_workers");
        console.Out.Should().NotContain("updated");
        factory.Verify(f => f.Create(It.IsAny<string?>()), Times.Never());
    }

    [Fact]
    public async Task Set_NonNumericValueForWorkerKey_IsRefused_WithoutCallingClient()
    {
        Mock<ICliClient> client = new();
        client
            .Setup(c =>
                c.PutAsync(ApiRoutes.Config, It.IsAny<HttpContent>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(true);

        Mock<ICliClientFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<string?>())).Returns(client.Object);

        using ConsoleCapture console = new();
        int exitCode = await RunSetAsync(factory.Object, "music_workers", "many");

        exitCode.Should().Be((int)ExitCode.ConfigurationError);
        console.Error.Should().Contain("many");
        console.Out.Should().NotContain("updated");
        factory.Verify(f => f.Create(It.IsAny<string?>()), Times.Never());
    }
}
