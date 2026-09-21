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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Ipc;
using ProtoBuf.Grpc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// A plugin in its own process must not be able to tell it moved.
/// <para>
/// Every facade is a proxy over the channel, so what is asserted here is that
/// each one names itself and its member on the wire, and that a refusal comes
/// back as the same exception an in-process plugin would have caught.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class RemoteContextTests
{
    [Theory]
    [InlineData("secrets", "GetAsync", "\"a-secret\"")]
    [InlineData("grants", "HasAsync", "true")]
    [InlineData("hub", "PushAsync", "null")]
    [InlineData("configuration", "HasConfiguration", "true")]
    public async Task EachFacade_NamesItselfAndItsMemberOnTheWire(
        string facade,
        string member,
        string payload
    )
    {
        RecordingBroker broker = new(PluginCallResponse.Value(payload));
        RemotePluginContext context = Build(broker);

        await Call(context, facade, member);

        broker.Last!.Facade.Should().Be(facade);
        broker.Last.Member.Should().Be(member);
    }

    [Fact]
    public async Task ARefusal_ReachesThePluginAsPluginRefusedException()
    {
        RecordingBroker broker = new(
            PluginCallResponse.Refused(
                new WireRefusal(
                    PluginRefusalCodes.CapabilityNotDeclared,
                    "radio",
                    "The plugin asked for a secret.",
                    "It did not declare the secrets capability.",
                    "Declare it in the manifest. Docs: /nomercy-plugins/handbook/capabilities",
                    "Blocked"
                )
            )
        );

        RemotePluginContext context = Build(broker);

        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            context.Secrets.GetAsync("token")
        );

        refused.Refusal.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
        refused.Refusal.Fix.Should().Contain("/nomercy-plugins/");
    }

    [Fact]
    public async Task AValueComesBackDeserialised_NotAsRawJson()
    {
        RecordingBroker broker = new(PluginCallResponse.Value("\"a-secret\""));

        string? secret = await Build(broker).Secrets.GetAsync("token");

        secret.Should().Be("a-secret");
    }

    /// <summary>
    /// The plugin id and its data folder are the host's own answer and never
    /// cross the channel. A round trip for a value that cannot change would
    /// make every plugin pay for the boundary on its first line.
    /// </summary>
    [Fact]
    public void TheIdAndDataFolder_AreAnsweredWithoutACall()
    {
        RecordingBroker broker = new(PluginCallResponse.Value("null"));
        RemotePluginContext context = Build(broker);

        context.PluginId.Should().NotBe(default(Ulid));
        context.DataFolderPath.Should().Be("C:/plugins/data/radio");
        broker.Calls.Should().Be(0);
    }

    private static Task Call(RemotePluginContext context, string facade, string member) =>
        facade switch
        {
            "secrets" => context.Secrets.GetAsync("token"),
            "grants" => context.Grants.HasAsync("network", "*.example.com"),
            "hub" => context.Hub.PushAsync("tick", null),
            "configuration" => Task.Run(() => context.Configuration.HasConfiguration()),
            _ => throw new ArgumentOutOfRangeException(nameof(facade)),
        };

    private static RemotePluginContext Build(RecordingBroker broker) =>
        new(
            new PluginHostLaunch(
                Ulid.NewUlid(),
                "C:/plugins/radio/Radio.dll",
                "C:/plugins/data/radio",
                "broker",
                "host",
                "token"
            ),
            broker
        );
}

internal sealed class RecordingBroker(PluginCallResponse response) : IPluginBrokerService
{
    public PluginCallRequest? Last { get; private set; }

    public int Calls { get; private set; }

    public Task<PluginCallResponse> CallAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        Last = request;
        Calls++;
        return Task.FromResult(response);
    }

    public Task<PluginCallResponse> PublishAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => CallAsync(request, context);

    public IAsyncEnumerable<PluginCallRequest> SubscribeAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => AsyncEnumerable.Empty<PluginCallRequest>();
}
