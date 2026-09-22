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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Ipc;
using NoMercy.Plugins.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The four facades whose answer is neither a permit nor a file: metadata,
/// notifications, users and the scheduler.
/// <para>
/// Each is gated by its own capability, and each is checked before the facade
/// is touched. A facade that ran and then refused has already done the thing
/// the refusal was for.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ServicesBrokerTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    [Theory]
    [InlineData("metadata", "QueryAsync", """{"provider":"*","kind":"Movie","title":"Arrival"}""")]
    [InlineData("users", "ListAsync", "{}")]
    [InlineData("scheduler", "RunNowAsync", """{"name":"sweep"}""")]
    public async Task EachFacadeRefusesBeforeItIsTouchedWhenTheCapabilityIsMissing(
        string facade,
        string member,
        string payload
    )
    {
        RecordingServices services = new();

        PluginCallResponse response = await Ask(services, Refusing(), facade, member, payload);

        response.Ok.Should().BeFalse();
        response.Refusal!.Code.Should().Be(PluginRefusalCodes.CapabilityNotDeclared);
        services.Touches.Should().Be(0);
    }

    [Fact]
    public async Task AMetadataQueryReachesTheProvidersTheOwnerConfigured()
    {
        RecordingServices services = new();

        PluginCallResponse response = await Ask(
            services,
            new FakeCapabilities(null),
            "metadata",
            nameof(IPluginMetadata.QueryAsync),
            """{"provider":"*","kind":"Movie","title":"Arrival"}"""
        );

        response.Ok.Should().BeTrue();
        services.LastQueryTitle.Should().Be("Arrival");
    }

    /// <summary>
    /// A notification names the person it is for. A plugin that could leave
    /// that out would reach the whole household from one user's page.
    /// </summary>
    [Fact]
    public async Task ANotificationCarriesTheUserItIsFor()
    {
        RecordingServices services = new();
        Ulid user = Ulid.NewUlid();

        await Ask(
            services,
            new FakeCapabilities(null),
            "notifications",
            nameof(IPluginNotifications.PushAsync),
            $$$"""{"user":"{{{user}}}","notification":{"titleKey":"plugin.scan.done","bodyKey":"plugin.scan.done.body"}}"""
        );

        services.LastUser.Should().NotBeNull();
        services.LastUser!.Value.Value.Should().Be(user);
    }

    [Fact]
    public async Task AJobTheOwnerAskedForNowReachesTheServersOwnQueue()
    {
        RecordingServices services = new();

        PluginCallResponse response = await Ask(
            services,
            new FakeCapabilities(null),
            "scheduler",
            nameof(IPluginScheduler.RunNowAsync),
            """{"name":"sweep"}"""
        );

        response.Ok.Should().BeTrue();
        services.LastJobName.Should().Be("sweep");
    }

    [Fact]
    public async Task ASchedulerMemberThatDoesNotCrossYet_SaysSoByName()
    {
        PluginCallResponse response = await Ask(
            new RecordingServices(),
            new FakeCapabilities(null),
            "scheduler",
            "Register"
        );

        response.Ok.Should().BeFalse();
        response.Refusal!.What.Should().Contain("Register");
    }

    private static Task<PluginCallResponse> Ask(
        RecordingServices services,
        IPluginCapabilityBroker capabilities,
        string facade,
        string member,
        string payloadJson = "{}"
    ) =>
        new PluginBrokerService(
            PluginId,
            capabilities,
            new RecordingSecrets(),
            new RecordingBinaries(),
            new FakeServerInfo(),
            new FakeStorageRoots(),
            new RecordingLibrary(),
            services,
            services,
            services,
            services
        ).CallAsync(new PluginCallRequest(PluginId.ToString(), facade, member, payloadJson, null));

    private static IPluginCapabilityBroker Refusing() =>
        new FakeCapabilities(
            new PluginRefusal(
                PluginRefusalCodes.CapabilityNotDeclared,
                "radio",
                "The plugin used a server service.",
                "It did not declare the capability that service belongs to.",
                "Declare it in the manifest. Docs: /nomercy-plugins/handbook/capabilities",
                PluginRefusalSeverity.Blocked
            )
        );
}

internal sealed class RecordingServices
    : IPluginMetadata,
        IPluginNotifications,
        IPluginUsers,
        IPluginScheduler
{
    public int Touches { get; private set; }

    public string? LastQueryTitle { get; private set; }

    public UserId? LastUser { get; private set; }

    public string? LastJobName { get; private set; }

    public Task<IReadOnlyList<PluginMetadataMatch>> QueryAsync(
        PluginMetadataQuery query,
        CancellationToken ct = default
    )
    {
        Touches++;
        LastQueryTitle = query.Title;

        return Task.FromResult<IReadOnlyList<PluginMetadataMatch>>([]);
    }

    public Task PushAsync(
        UserId? user,
        PluginNotification notification,
        CancellationToken ct = default
    )
    {
        Touches++;
        LastUser = user;

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PluginUserIdentity>> ListAsync(CancellationToken ct = default)
    {
        Touches++;

        return Task.FromResult<IReadOnlyList<PluginUserIdentity>>([]);
    }

    public void Register(PluginScheduledJob job) => Touches++;

    public Task<JobId> RunOnceAsync(
        string name,
        DateTimeOffset when,
        CancellationToken ct = default
    )
    {
        Touches++;
        LastJobName = name;

        return Task.FromResult(new JobId(Ulid.NewUlid()));
    }

    public Task<JobId> RunNowAsync(string name, CancellationToken ct = default)
    {
        Touches++;
        LastJobName = name;

        return Task.FromResult(new JobId(Ulid.NewUlid()));
    }

    public void StartWorker(PluginWorker worker) => Touches++;

    public Task StopWorkerAsync(string name, CancellationToken ct = default)
    {
        Touches++;

        return Task.CompletedTask;
    }

    public Task<PluginWorkerState> WorkerStateAsync(string name, CancellationToken ct = default)
    {
        Touches++;

        return Task.FromResult(PluginWorkerState.Stopped);
    }
}
