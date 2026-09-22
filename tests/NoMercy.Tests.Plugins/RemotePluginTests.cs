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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.OutOfProcess;
using ProtoBuf.Grpc;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin running somewhere else, as everything above the loader sees it.
/// <para>
/// The registry, the dashboard and every screen hold an IPlugin. If a remote
/// one cannot be one of those, the isolation setting can never be honored
/// without changing every caller, which is why the load path stopped here.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class RemotePluginTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private static PluginDescription Described() =>
        new(PluginId, "Internet Radio", "Stations, over the internet", new Version(2, 1));

    /// <summary>
    /// The dashboard lists plugins whose process is stopped or crashed. Asking
    /// the process for its own name would leave exactly those nameless, on the
    /// screen that offers to start them.
    /// </summary>
    [Fact]
    public void TheNameAndVersionAreAnsweredWithNoProcessRunning()
    {
        RecordingHost host = new(refuse: true);
        RemotePlugin plugin = new(Described(), host);

        plugin.Name.Should().Be("Internet Radio");
        plugin.Id.Should().Be(PluginId);
        plugin.Version.Should().Be(new Version(2, 1));
        host.Calls.Should().Be(0);
    }

    [Fact]
    public void InitializeTellsTheOtherProcessToStart()
    {
        RecordingHost host = new();
        RemotePlugin plugin = new(Described(), host);

        plugin.Initialize(null!);

        host.Initialized.Should().Be(1);
    }

    /// <summary>
    /// The context belongs to the server's heap, and an object cannot cross a
    /// process boundary. Handing it over would give the plugin a reference
    /// into the server it is being isolated from.
    /// </summary>
    [Fact]
    public void InitializeSendsNoContextAcrossTheBoundary()
    {
        RecordingHost host = new();
        RemotePlugin plugin = new(Described(), host);

        plugin.Initialize(null!);

        host.LastPayload.Should().Be("null");
    }

    /// <summary>
    /// A plugin that would not start has to reach the owner as a refusal. A
    /// quiet return would register a plugin the server believes is running and
    /// that answers nothing.
    /// </summary>
    [Fact]
    public void AProcessThatRefusesToInitializeIsNotTreatedAsStarted()
    {
        RemotePlugin plugin = new(Described(), new RecordingHost(refuse: true));

        Assert.Throws<PluginRefusedException>(() => plugin.Initialize(null!));
    }

    /// <summary>
    /// A plugin may be holding a recording or a half-written import, so it
    /// gets to run its own shutdown first. The stop still happens: one that
    /// ignores the request does not outlive the server that started it.
    /// </summary>
    [Fact]
    public void DisposeAsksTheProcessToStopAndThenStopsIt()
    {
        RecordingHost host = new();
        int stopped = 0;

        RemotePlugin plugin = new(
            Described(),
            host,
            () =>
            {
                stopped++;
                return Task.CompletedTask;
            }
        );

        plugin.Dispose();

        host.ShutdownsAsked.Should().Be(1);
        stopped.Should().Be(1);
    }

    /// <summary>
    /// A channel that is already gone is the thing being asked for. Throwing
    /// here would leave the process alive because the tidy-up died first.
    /// </summary>
    [Fact]
    public void AProcessAlreadyGoneIsStillStopped()
    {
        int stopped = 0;

        RemotePlugin plugin = new(
            Described(),
            new RecordingHost(throws: true),
            () =>
            {
                stopped++;
                return Task.CompletedTask;
            }
        );

        plugin.Dispose();

        stopped.Should().Be(1);
    }

    private sealed class RecordingHost(bool refuse = false, bool throws = false)
        : IPluginHostService
    {
        public int Calls { get; private set; }

        public int Initialized { get; private set; }

        public int ShutdownsAsked { get; private set; }

        public string? LastPayload { get; private set; }

        public Task<PluginCallResponse> InitializeAsync(
            PluginCallRequest request,
            CallContext context = default
        )
        {
            Calls++;
            Initialized++;
            LastPayload = request.PayloadJson;

            if (throws)
                throw new InvalidOperationException("the channel is gone");

            return Task.FromResult(
                refuse
                    ? PluginCallResponse.Refused(
                        new WireRefusal(
                            PluginRefusalCodes.HostServicesRemoved,
                            request.PluginId,
                            "The plugin process was asked to start.",
                            "It refused.",
                            "Read the server log.",
                            PluginRefusalSeverity.Blocked.ToString()
                        )
                    )
                    : PluginCallResponse.Value("{}")
            );
        }

        public Task<PluginCallResponse> InvokeAsync(
            PluginCallRequest request,
            CallContext context = default
        )
        {
            Calls++;
            return Task.FromResult(PluginCallResponse.Value("{}"));
        }

        public Task<PluginHealthSnapshot> HealthAsync(
            PluginCallRequest request,
            CallContext context = default
        )
        {
            Calls++;
            return Task.FromResult(new PluginHealthSnapshot(0, TimeSpan.Zero, 0, 0, 0, 0, null));
        }

        public Task<PluginCallResponse> ShutdownAsync(
            PluginCallRequest request,
            CallContext context = default
        )
        {
            Calls++;
            ShutdownsAsked++;

            if (throws)
                throw new InvalidOperationException("the channel is gone");

            return Task.FromResult(PluginCallResponse.Value("{}"));
        }
    }
}
