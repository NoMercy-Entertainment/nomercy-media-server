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

using System.Reflection;
using System.Timers;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Discovery;
using Sharpcaster;
using Xunit;
using Timer = System.Timers.Timer;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: Sharpcaster's built-in heartbeat must be fully neutered
/// process-wide (see the class-level comment on DisableSharpcasterHeartbeat)
/// or an async-void SocketException on disconnect kills the whole server
/// process. NeutralizeTimer must stop the timer, disable AutoReset, and swap
/// its private _onIntervalElapsed delegate to a no-op — all without
/// disposing it (Sharpcaster later flips Enabled=true on a PONG, which
/// throws on a disposed timer). These exercise the real reflection-based
/// logic against a real System.Timers.Timer and a real (unconnected)
/// Sharpcaster ChromecastClient — no live Chromecast device involved.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChromeCastServiceReflectionUtilityTests
{
    private sealed class NoOpNetworkDiscovery : INetworkDiscovery
    {
        public string InternalIp { get; set; } = "192.168.1.1";
        public string RegistrationInternalIp => InternalIp;
        public string ExternalIp { get; set; } = "1.2.3.4";
        public string? InternalIpV6 => null;
        public string? ExternalIpV6 { get; set; }
        public string InternalDomain => string.Empty;
        public string InternalAddress => string.Empty;
        public string ExternalDomain => string.Empty;
        public string ExternalAddress => string.Empty;
        public string DirectExternalAddress => ExternalAddress;
        public string? ExternalAddressV6 => null;
        public bool Ipv6Enabled => false;

        public Task DiscoverExternalIpAsync() => Task.CompletedTask;

        public Task ForceRediscoveryAsync() => Task.CompletedTask;

        public Task<bool> IsPortOpenAsync() => Task.FromResult(false);

        public Task RemovePortMappingsAsync() => Task.CompletedTask;
    }

    private static ChromeCastService BuildService() =>
        new(NullLogger<ChromeCastService>.Instance, new NoOpNetworkDiscovery());

    [Fact]
    public void NeutralizeTimer_StopsTheTimer_AndDisablesAutoReset()
    {
        ChromeCastService service = BuildService();
        Timer timer = new(1000) { AutoReset = true };
        timer.Start();

        service.NeutralizeTimer(timer);

        Assert.False(timer.Enabled);
        Assert.False(timer.AutoReset);
    }

    [Fact]
    public void NeutralizeTimer_ReplacesElapsedHandler_SoOriginalNeverFires()
    {
        ChromeCastService service = BuildService();
        Timer timer = new(1000);
        bool originalHandlerFired = false;
        timer.Elapsed += (_, _) => originalHandlerFired = true;

        service.NeutralizeTimer(timer);

        // Force-invoke whatever handler is now wired to the timer's private
        // _onIntervalElapsed field — after neutralizing, that must not be the
        // original handler we attached above.
        FieldInfo? field = typeof(Timer).GetField(
            "_onIntervalElapsed",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.NotNull(field);
        object? handler = field!.GetValue(timer);
        Assert.NotNull(handler);

        ((ElapsedEventHandler)handler!).Invoke(timer, null!);

        Assert.False(originalHandlerFired);
    }

    [Fact]
    public void NeutralizeTimer_DoesNotDisposeTheTimer()
    {
        ChromeCastService service = BuildService();
        Timer timer = new(1000);

        service.NeutralizeTimer(timer);

        // A disposed Timer throws ObjectDisposedException from set_Enabled —
        // this must not throw, proving the timer was left alive.
        Exception? ex = Record.Exception(() => timer.Enabled = true);
        Assert.Null(ex);
        timer.Dispose();
    }

    [Fact]
    public void NeutralizeTimer_CalledTwice_DoesNotThrow()
    {
        ChromeCastService service = BuildService();
        Timer timer = new(1000);

        Exception? ex = Record.Exception(() =>
        {
            service.NeutralizeTimer(timer);
            service.NeutralizeTimer(timer);
        });

        Assert.Null(ex);
        timer.Dispose();
    }

    private sealed class OwnerWithPrivateTimer
    {
        private readonly Timer _timer = new(1000);

        public Timer Timer => _timer;
    }

    [Fact]
    public void NeutralizeTimersIn_FindsAndStopsPrivateTimerField()
    {
        ChromeCastService service = BuildService();
        OwnerWithPrivateTimer owner = new();
        owner.Timer.Start();

        service.NeutralizeTimersIn(owner);

        Assert.False(owner.Timer.Enabled);
        owner.Timer.Dispose();
    }

    private sealed class OwnerWithNoTimerFields
    {
        private readonly string _label = "no timers here";

        public string Label => _label;
    }

    [Fact]
    public void NeutralizeTimersIn_ObjectWithoutTimerFields_DoesNotThrow()
    {
        ChromeCastService service = BuildService();

        Exception? ex = Record.Exception(() =>
            service.NeutralizeTimersIn(new OwnerWithNoTimerFields())
        );

        Assert.Null(ex);
    }

    [Fact]
    public void BuildClient_ReturnsNonNullClient_AndDoesNotThrow()
    {
        ChromeCastService service = BuildService();

        ChromecastClient? client = null;
        Exception? ex = Record.Exception(() => client = service.BuildClient("Living Room TV"));

        Assert.Null(ex);
        Assert.NotNull(client);
    }

    [Fact]
    public void DisableSharpcasterHeartbeat_AgainstBareUnconnectedClient_DoesNotThrow()
    {
        ChromeCastService service = BuildService();
        ChromecastClient client = new();

        Exception? ex = Record.Exception(() => service.DisableSharpcasterHeartbeat(client));

        Assert.Null(ex);
    }

    [Fact]
    public void DisableSharpcasterHeartbeat_CalledTwice_DoesNotThrow()
    {
        ChromeCastService service = BuildService();
        ChromecastClient client = new();

        Exception? ex = Record.Exception(() =>
        {
            service.DisableSharpcasterHeartbeat(client);
            service.DisableSharpcasterHeartbeat(client);
        });

        Assert.Null(ex);
    }

    // -- BuildLaunchJson: all four LAUNCH-payload permutations.

    [Fact]
    public void BuildLaunchJson_NoCustomData_AndroidReceiver_ContainsAndroidLaunchOptions()
    {
        ChromeCastService service = BuildService();

        string json = service.BuildLaunchJson(1, null, useAndroidReceiver: true);

        Assert.Contains("\"appId\":\"925B4C3C\"", json);
        Assert.Contains("\"requestId\":1", json);
        Assert.Contains("ANDROID_TV", json);
        Assert.Contains("androidReceiverCompatible", json);
        Assert.DoesNotContain("customData", json);
    }

    [Fact]
    public void BuildLaunchJson_NoCustomData_WebReceiver_ContainsWebAppType()
    {
        ChromeCastService service = BuildService();

        string json = service.BuildLaunchJson(2, null, useAndroidReceiver: false);

        Assert.Contains("\"appId\":\"925B4C3C\"", json);
        Assert.Contains("\"requestId\":2", json);
        Assert.Contains("\"WEB\"", json);
        Assert.DoesNotContain("androidReceiverCompatible", json);
        Assert.DoesNotContain("customData", json);
    }

    [Fact]
    public void BuildLaunchJson_WithCustomData_AndroidReceiver_IncludesCustomData()
    {
        ChromeCastService service = BuildService();

        string json = service.BuildLaunchJson(
            3,
            new { accessToken = "abc" },
            useAndroidReceiver: true
        );

        Assert.Contains("androidReceiverCompatible", json);
        Assert.Contains("customData", json);
        Assert.Contains("abc", json);
    }

    [Fact]
    public void BuildLaunchJson_WithCustomData_WebReceiver_IncludesCustomData()
    {
        ChromeCastService service = BuildService();

        string json = service.BuildLaunchJson(
            4,
            new { accessToken = "xyz" },
            useAndroidReceiver: false
        );

        Assert.Contains("\"WEB\"", json);
        Assert.Contains("customData", json);
        Assert.Contains("xyz", json);
        Assert.DoesNotContain("androidReceiverCompatible", json);
    }

    [Fact]
    public void BuildLaunchJson_RequestIdIsEmbeddedVerbatim()
    {
        ChromeCastService service = BuildService();

        string json = service.BuildLaunchJson(999, null, useAndroidReceiver: true);

        Assert.Contains("\"requestId\":999", json);
    }
}
