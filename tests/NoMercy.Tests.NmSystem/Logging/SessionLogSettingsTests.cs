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

using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Logging;
using Xunit;

namespace NoMercy.Tests.NmSystem.Logging;

[Trait("Category", "Unit")]
public class SessionLogSettingsTests
{
    [Fact]
    public void ApplyEnvironment_NothingSet_SessionAndCastDefaultToInformation()
    {
        NoMercyLoggerOptions options = new();

        options.ApplyEnvironment(_ => null);

        options.CategoryLevels["session"].Should().Be(LogLevel.Information);
        options.CategoryLevels["cast"].Should().Be(LogLevel.Information);
    }

    [Fact]
    public void ApplyEnvironment_LevelSet_OverridesOnlyThatCategory()
    {
        NoMercyLoggerOptions options = new();

        options.ApplyEnvironment(name => name == "NOMERCY_LOG_LEVEL_CAST" ? "Debug" : null);

        options.CategoryLevels["cast"].Should().Be(LogLevel.Debug);
        options.CategoryLevels["session"].Should().Be(LogLevel.Information);
    }

    [Fact]
    public void ApplyEnvironment_NotALevelName_KeepsTheDefault()
    {
        NoMercyLoggerOptions options = new();

        options.ApplyEnvironment(name => name == "NOMERCY_LOG_LEVEL_SESSION" ? "loud" : null);

        options.CategoryLevels["session"].Should().Be(LogLevel.Information);
    }

    [Theory]
    [InlineData("NoMercy.Api.Hubs.Filters.SessionRequestLogFilter", "session")]
    [InlineData("NoMercy.Api.Hubs.VideoHub", "session")]
    [InlineData("NoMercy.Networking.Messaging.ClientMessenger", "session")]
    [InlineData("NoMercy.Setup.Cast.CastPanelWakeLauncher", "cast")]
    [InlineData("NoMercy.Api.Services.Cast.ServerCastWaker", "cast")]
    public void ResolveSource_SessionAndCastClasses_MapToTheirCategory(string source, string key)
    {
        LogCategories.ResolveSource(source).Key.Should().Be(key);
    }

    [Fact]
    public void FromEnvironment_QuietMethodsSet_ReplacesTheDefaultList()
    {
        SessionLogPolicy policy = SessionLogPolicy.FromEnvironment(name =>
            name == SessionLogPolicy.QuietMethodsVariable ? "Ping, Poll" : null
        );

        policy.RequestLevel("Poll").Should().Be(LogLevel.Debug);
        policy.RequestLevel("SetTime").Should().Be(LogLevel.Information);
    }

    [Fact]
    public void RequestLevel_Default_TimeReportIsDebugAndCommandIsInformation()
    {
        SessionLogPolicy policy = new();

        policy.RequestLevel("SetTime").Should().Be(LogLevel.Debug);
        policy.RequestLevel("ChangeDeviceCommand").Should().Be(LogLevel.Information);
    }

    [Fact]
    public void BroadcastLevel_SecondStateEventInsideTheInterval_IsDebug()
    {
        SessionLogPolicy policy = new(stateInterval: TimeSpan.FromMinutes(5));
        Guid user = Guid.NewGuid();

        LogLevel first = policy.BroadcastLevel("MusicPlayerState", user);
        LogLevel second = policy.BroadcastLevel("MusicPlayerState", user);

        first.Should().Be(LogLevel.Information);
        second.Should().Be(LogLevel.Debug);
    }
}
