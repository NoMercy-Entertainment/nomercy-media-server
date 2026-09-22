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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;
using Xunit;

namespace NoMercy.Tests.PluginHost;

[Trait("Category", "Unit")]
public class PluginHostBootTests
{
    [Fact]
    public void Launch_WithoutAnAssemblyPath_RefusesWithATeachingMessage()
    {
        PluginHostLaunch
            .TryRead(new Dictionary<string, string?>(), out _, out WireRefusal? refusal)
            .Should()
            .BeFalse();

        refusal!.Code.Should().Be("PLUGIN_HOST_UNAVAILABLE");
        refusal.Why.Should().Contain(PluginChannelEnvironment.PluginId);
        refusal.Fix.Should().Contain("/nomercy-plugins/handbook/runtime-and-isolation");
    }

    [Fact]
    public void Launch_MissingOnlyTheAssembly_NamesThatVariableAndNoOther()
    {
        Dictionary<string, string?> environment = Complete();
        environment[PluginChannelEnvironment.AssemblyPath] = "";

        PluginHostLaunch.TryRead(environment, out _, out WireRefusal? refusal).Should().BeFalse();

        refusal!.Why.Should().Contain(PluginChannelEnvironment.AssemblyPath);
    }

    [Fact]
    public void Launch_WithEveryVariable_ReadsThePluginIdAndTheEndpoints()
    {
        Dictionary<string, string?> environment = Complete();

        PluginHostLaunch
            .TryRead(environment, out PluginHostLaunch? launch, out _)
            .Should()
            .BeTrue();

        launch!.PluginId.Should().Be(Ulid.Parse(environment[PluginChannelEnvironment.PluginId]!));
        launch.Token.Should().Be("0123456789abcdef");
        launch.HostEndpoint.Should().Be("NoMercy.Plugin.x.host");
    }

    [Fact]
    public async Task HostService_WithTheWrongToken_RefusesAndNeverTouchesThePlugin()
    {
        FakeHostedPlugin plugin = new();
        PluginHostService service = new(plugin, "right-token");

        PluginCallResponse response = await service.InvokeAsync(
            new(Ulid.NewUlid().ToString(), "plugin", "GetViewAsync", "{}", null),
            PluginHostService.ContextWithToken("wrong-token")
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be("PLUGIN_HOST_UNAVAILABLE");
        plugin.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task HostService_WithNoTokenAtAll_RefusesTheSameWay()
    {
        FakeHostedPlugin plugin = new();
        PluginHostService service = new(plugin, "right-token");

        PluginCallResponse response = await service.InvokeAsync(
            new(Ulid.NewUlid().ToString(), "plugin", "GetViewAsync", "{}", null)
        );

        response.Ok.Should().BeFalse();
        plugin.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task HostService_WithTheLaunchToken_ReachesThePlugin()
    {
        FakeHostedPlugin plugin = new();
        PluginHostService service = new(plugin, "right-token");

        PluginCallResponse response = await service.InvokeAsync(
            new(Ulid.NewUlid().ToString(), "plugin", "GetViewAsync", "{}", null),
            PluginHostService.ContextWithToken("right-token")
        );

        response.Ok.Should().BeTrue();
        plugin.Invocations.Should().Be(1);
    }

    private static Dictionary<string, string?> Complete() =>
        new()
        {
            [PluginChannelEnvironment.PluginId] = Ulid.NewUlid().ToString(),
            [PluginChannelEnvironment.AssemblyPath] = "C:/plugins/radio/Radio.dll",
            [PluginChannelEnvironment.DataFolder] = "C:/plugins/data/radio",
            [PluginChannelEnvironment.BrokerEndpoint] = "NoMercy.Plugin.x.broker",
            [PluginChannelEnvironment.HostEndpoint] = "NoMercy.Plugin.x.host",
            [PluginChannelEnvironment.Token] = "0123456789abcdef",
        };
}

file sealed class FakeHostedPlugin : IHostedPlugin
{
    public int Invocations { get; private set; }

    public IPlugin Instance => throw new NotSupportedException();

    public Task<string> InvokeAsync(
        string member,
        string payloadJson,
        CancellationToken cancellationToken
    )
    {
        Invocations++;
        return Task.FromResult("{}");
    }
}

/// <summary>
/// What a plugin may be asked to do is the set of entry points it implements.
/// Dispatch by reflection over the member name would call whatever a caller
/// could spell, so the switch refuses anything it does not recognise.
/// </summary>
[Trait("Category", "Unit")]
public class PluginDispatchTests
{
    [Fact]
    public async Task AMemberThePluginDoesNotImplement_IsRefusedByName()
    {
        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            PluginDispatch.InvokeAsync(new BarePlugin(), "GetViewAsync", "{}", default)
        );

        refused.Refusal.What.Should().Contain("GetViewAsync");
        refused.Refusal.Fix.Should().Contain("/nomercy-plugins/");
    }

    [Fact]
    public async Task AMemberNobodyDeclared_IsRefusedRatherThanCalled()
    {
        await Assert.ThrowsAsync<PluginRefusedException>(() =>
            PluginDispatch.InvokeAsync(new BarePlugin(), "DeleteEverything", "{}", default)
        );
    }

    [Fact]
    public async Task AScheduledPluginAskedToRun_RunsTheNamedJob()
    {
        ScheduledPlugin plugin = new();

        await PluginDispatch.InvokeAsync(
            plugin,
            "ExecuteAsync",
            """{"jobName":"cycle"}""",
            default
        );

        plugin.Ran.Should().Be("cycle");
    }
}

file sealed class BarePlugin : IPlugin
{
    public string Name => "Bare";

    public string Description => "Implements no entry point.";

    public Ulid Id { get; } = Ulid.NewUlid();

    public Version Version => new(1, 0);

    public void Initialize(IPluginContext context) { }

    public void Dispose() { }
}

file sealed class ScheduledPlugin : IScheduledTaskPlugin
{
    public string? Ran { get; private set; }

    public string CronExpression => "0 * * * *";

    public string Name => "Scheduled";

    public string Description => "Runs on a cadence.";

    public Ulid Id { get; } = Ulid.NewUlid();

    public Version Version => new(1, 0);

    public Task ExecuteAsync(CancellationToken ct = default)
    {
        Ran = "";
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(string jobName, CancellationToken ct = default)
    {
        Ran = jobName;
        return Task.CompletedTask;
    }

    public void Initialize(IPluginContext context) { }

    public void Dispose() { }
}
