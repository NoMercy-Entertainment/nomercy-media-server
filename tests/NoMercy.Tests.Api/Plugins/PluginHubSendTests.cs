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

using System.Security.Claims;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Api.Plugins;
using NoMercy.Database;
using NoMercy.Database.Activity;
using NoMercy.Networking.Messaging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Hub;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// A plugin registers a hub method with <c>Handle</c>, and the handler refuses
/// any message that does not say who sent it. The hub has to resolve that
/// caller, or no such method ever runs.
/// </summary>
[Trait("Category", "Unit")]
public class PluginHubSendTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000019");
    private static readonly Guid Owner = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task A_method_registered_with_Handle_runs_for_the_caller_and_answers_them()
    {
        PluginDelegateHubHandler handler = new(Plugin);
        PluginCaller? seen = null;
        handler.Register(
            "ping",
            (caller, _, _) =>
            {
                seen = caller;
                return Task.FromResult<object?>("pong");
            }
        );
        Mock<ISingleClientProxy> client = new();
        PluginHub hub = Hub(new HandingRouter(handler), PluginAccess.Owned, client);

        bool routed = await hub.Send(Plugin.ToString(), "ping", null);

        routed.Should().BeTrue();
        seen.Should().NotBeNull();
        seen!.Id.Should().Be(new UserId(new Ulid(Owner)));
        seen.Role.Should().Be(PluginRole.Owner);
        seen.Access.Should().Be(PluginAccess.Owned);
        client.Verify(
            proxy =>
                proxy.SendCoreAsync(
                    "PluginMessage",
                    It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    /// <summary>Somebody who cannot see the plugin cannot reach its hub methods either.</summary>
    [Fact]
    public async Task A_caller_without_access_to_the_plugin_reaches_nothing()
    {
        CapturingRouter router = new();
        PluginHub hub = Hub(router, PluginAccess.None, new Mock<ISingleClientProxy>());

        bool routed = await hub.Send(Plugin.ToString(), "ping", null);

        routed.Should().BeFalse();
        router.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task The_message_carries_the_access_the_resolver_gave()
    {
        CapturingRouter router = new();
        PluginHub hub = Hub(router, PluginAccess.Shared, new Mock<ISingleClientProxy>());

        await hub.Send(Plugin.ToString(), "ping", JsonNode.Parse("""{"n":1}"""));

        PluginHubMessage message = router.Messages.Single();
        message.Caller!.Access.Should().Be(PluginAccess.Shared);
        message.Caller.Locale.Should().Be("nl-NL");
        message.Payload!["n"]!.GetValue<int>().Should().Be(1);
    }

    private static PluginHub Hub(
        IPluginHubRouter router,
        PluginAccess access,
        Mock<ISingleClientProxy> client
    )
    {
        Mock<IPluginAccessResolver> resolver = new();
        resolver.Setup(r => r.Resolve(Plugin, Owner)).Returns(access);

        PluginHub hub = new(
            new Mock<IHttpContextAccessor>().Object,
            new Mock<IDbContextFactory<MediaContext>>().Object,
            new ConnectedClients(),
            new Mock<IActivityLogger>().Object,
            router,
            resolver.Object
        );

        Mock<HubCallerContext> context = new();
        context
            .Setup(c => c.User)
            .Returns(
                new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, Owner.ToString()),
                            new Claim(ClaimTypes.Role, "owner"),
                            new Claim("name", "Owner"),
                        ],
                        "TestAuth"
                    )
                )
            );
        context.Setup(c => c.ConnectionId).Returns("connection-1");

        // The request that opened the connection, which is where a hub reads
        // the viewer's language from.
        DefaultHttpContext opened = new();
        opened.Request.Headers.AcceptLanguage = "nl-NL";
        FeatureCollection features = new();
        features.Set<IHttpContextFeature>(new OpenedBy(opened));
        context.Setup(c => c.Features).Returns(features);
        hub.Context = context.Object;

        Mock<IHubCallerClients> clients = new();
        clients.Setup(c => c.Caller).Returns(client.Object);
        hub.Clients = clients.Object;

        return hub;
    }

    private sealed class OpenedBy(HttpContext context) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    /// <summary>Hands every message straight to the plugin's delegate handler.</summary>
    private sealed class HandingRouter(PluginDelegateHubHandler handler) : IPluginHubRouter
    {
        public void Register(IPluginHubHandler hubHandler) { }

        public void Unregister(Ulid pluginId) { }

        public PluginDelegateHubHandler DelegateHandlerFor(Ulid pluginId) => handler;

        public async Task<bool> RouteAsync(
            Ulid pluginId,
            PluginHubMessage message,
            IPluginHubClient client,
            CancellationToken ct
        )
        {
            await handler.HandleAsync(message, client, ct);
            return true;
        }
    }

    private sealed class CapturingRouter : IPluginHubRouter
    {
        public List<PluginHubMessage> Messages { get; } = [];

        public void Register(IPluginHubHandler hubHandler) { }

        public void Unregister(Ulid pluginId) { }

        public PluginDelegateHubHandler DelegateHandlerFor(Ulid pluginId) => new(pluginId);

        public Task<bool> RouteAsync(
            Ulid pluginId,
            PluginHubMessage message,
            IPluginHubClient client,
            CancellationToken ct
        )
        {
            Messages.Add(message);
            return Task.FromResult(true);
        }
    }
}
