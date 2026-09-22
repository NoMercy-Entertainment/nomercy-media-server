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
using NoMercy.PluginHost;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.OutOfProcess;
using ProtoBuf.Grpc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// One call, over the transport the two processes really use.
/// <para>
/// Every other test in this project hands the proxies a broker object in the
/// same heap, which says nothing about whether a named pipe or a unix socket
/// carries the call. Both halves built and neither had ever been connected:
/// the host registered no broker client at all, so the context could not have
/// been constructed in a real run.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class BrokerChannelTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private const string Token = "3F2A9C01B7E84D6A";

    private static PluginHostLaunch Launch(string token) =>
        new(
            PluginId,
            "plugin.dll",
            Path.GetTempPath(),
            PluginChannelEndpoints.BrokerFor(PluginId),
            PluginChannelEndpoints.HostFor(PluginId),
            token
        );

    [Fact]
    public async Task ACallCrossesTheRealTransportAndTheAnswerComesBack()
    {
        EchoBroker broker = new(PluginCallResponse.Value("""{"platform":"windows"}"""));
        await using PluginBrokerEndpoint endpoint = new(PluginId, Token, broker);

        await endpoint.StartAsync();

        using BrokerChannel channel = new(Launch(Token));

        PluginCallResponse response = await channel.CallAsync(
            new PluginCallRequest(PluginId.ToString(), "server", "PlatformAsync", "null", null)
        );

        response.Ok.Should().BeTrue();
        response.PayloadJson.Should().Contain("windows");
        broker.Last!.Facade.Should().Be("server");
    }

    /// <summary>
    /// The endpoint is local, and local is not the same as ours. Without the
    /// token a second plugin on the same machine opens this pipe and calls as
    /// the first one, holding the first one's grants.
    /// </summary>
    [Fact]
    public async Task ACallWithoutTheLaunchTokenNeverReachesTheBroker()
    {
        EchoBroker broker = new(PluginCallResponse.Value("null"));
        await using PluginBrokerEndpoint endpoint = new(PluginId, Token, broker);

        await endpoint.StartAsync();

        using BrokerChannel channel = new(Launch("not-the-token"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            channel.CallAsync(
                new PluginCallRequest(PluginId.ToString(), "server", "PlatformAsync", "null", null)
            )
        );

        broker.Calls.Should().Be(0);
    }

    private sealed class EchoBroker(PluginCallResponse response) : IPluginBrokerService
    {
        public int Calls { get; private set; }

        public PluginCallRequest? Last { get; private set; }

        public Task<PluginCallResponse> CallAsync(
            PluginCallRequest request,
            CallContext context = default
        )
        {
            Calls++;
            Last = request;

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
}
