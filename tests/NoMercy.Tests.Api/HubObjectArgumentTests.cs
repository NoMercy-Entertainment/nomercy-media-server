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
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// A hub method with an <c>object</c> parameter must bind a JSON object or array, not
/// only a scalar. These tests go through the real SignalR pipeline, because the
/// argument is bound by the hub protocol with JsonHelper.Settings before the method runs.
/// The test user has nothing playing, so a well-formed command answers ok.
/// </summary>
[Trait("Category", "Integration")]
public class HubObjectArgumentTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public HubObjectArgumentTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<HubConnection> ConnectAsync(string hubPath)
    {
        TestServer server = _factory.Server;
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(server.BaseAddress, hubPath),
                options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                        await server
                            .CreateWebSocketClient()
                            .ConnectAsync(context.Uri, cancellationToken);
                }
            )
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static async Task<JsonElement> PlaybackAsync(
        HubConnection connection,
        string command,
        object? data
    )
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        return await connection.InvokeAsync<JsonElement>(
            "PlaybackCommand",
            command,
            data,
            timeout.Token
        );
    }

    [Fact]
    public async Task VideoHub_SeekWithATimeObject_IsAccepted()
    {
        await using HubConnection connection = await ConnectAsync("videoHub");

        JsonElement result = await PlaybackAsync(connection, "seek", new { time = 10 });

        result.GetProperty("ok").GetBoolean().Should().BeTrue(result.ToString());
    }

    [Fact]
    public async Task VideoHub_SeekWithANumber_IsStillAccepted()
    {
        await using HubConnection connection = await ConnectAsync("videoHub");

        JsonElement result = await PlaybackAsync(connection, "seek", 10);

        result.GetProperty("ok").GetBoolean().Should().BeTrue(result.ToString());
    }

    [Theory]
    [InlineData("seek")]
    [InlineData("volume")]
    public async Task VideoHub_AnObjectWithoutAUsableValue_IsInvalidInput(string command)
    {
        await using HubConnection connection = await ConnectAsync("videoHub");

        JsonElement result = await PlaybackAsync(connection, command, new { x = 1 });

        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("error_code").GetString().Should().Be("invalid_input");
    }

    [Fact]
    public async Task MusicHub_SeekWithAnObject_IsBound_AndRefusedAsInvalidInput()
    {
        await using HubConnection connection = await ConnectAsync("musicHub");

        JsonElement result = await PlaybackAsync(connection, "seek", new { time = 10 });

        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("error_code").GetString().Should().Be("invalid_input");
    }

    [Fact]
    public async Task MusicHub_SeekWithANumber_IsStillAccepted()
    {
        await using HubConnection connection = await ConnectAsync("musicHub");

        JsonElement result = await PlaybackAsync(connection, "seek", 10);

        result.GetProperty("ok").GetBoolean().Should().BeTrue(result.ToString());
    }
}
