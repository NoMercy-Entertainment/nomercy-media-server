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

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using NoMercy.Service;

namespace NoMercy.Tests.Api.Infrastructure;

/// <summary>
/// A real SignalR client connected to a hub of the test host over its in-memory
/// WebSocket, so a test goes through argument binding, hub authorization and filters
/// exactly as a client does. Every hub accepts only WebSockets.
/// </summary>
public static class HubTestConnection
{
    /// <param name="userId">
    /// The test user to connect as; null is <see cref="TestAuthHandler.DefaultUserId"/>,
    /// the owner.
    /// </param>
    public static async Task<HubConnection> ConnectAsync(
        WebApplicationFactory<Startup> factory,
        string hubPath,
        Guid? userId = null
    )
    {
        TestServer server = factory.Server;
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(server.BaseAddress, hubPath),
                options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                    {
                        WebSocketClient client = server.CreateWebSocketClient();
                        if (userId is { } id)
                            client.ConfigureRequest = request =>
                                request.Headers[TestAuthDefaults.TestUserIdHeader] = id.ToString();
                        return await client.ConnectAsync(context.Uri, cancellationToken);
                    };
                }
            )
            .Build();
        await connection.StartAsync();
        return connection;
    }
}
