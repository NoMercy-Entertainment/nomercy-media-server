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

using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Moq;
using NoMercy.Api.Services;
using Xunit;

namespace NoMercy.Tests.Api.Services;

[Trait("Category", "Unit")]
public sealed class ServerRestarterTests
{
    private readonly Mock<IHostApplicationLifetime> _lifetime = new();
    private readonly Dictionary<string, string?> _environment = [];
    private readonly List<ProcessStartInfo> _spawned = [];
    private readonly List<int> _exitCodes = [];
    private readonly ProcessStartInfo _relaunch = new("server") { UseShellExecute = false };

    [Fact]
    public void Begin_UnderASupervisor_SpawnsNothing()
    {
        _environment["INVOCATION_ID"] = "0123456789abcdef";

        ServerRestartOutcome outcome = Restarter(_ => true).Begin();

        outcome.Should().Be(new ServerRestartOutcome("supervisor", null));
        _spawned.Should().BeEmpty();
    }

    [Fact]
    public void Begin_WithoutASupervisor_SpawnsTheRelaunch()
    {
        ServerRestartOutcome outcome = Restarter(_ => true).Begin();

        outcome.Should().Be(new ServerRestartOutcome("relaunch", null));
        _spawned.Should().Equal(_relaunch);
    }

    [Fact]
    public void FailedSpawn_ReportsTheReason_AndNeverStopsTheServer()
    {
        ServerRestarter restarter = Restarter(_ => throw new InvalidOperationException("no exe"));

        ServerRestartOutcome outcome = restarter.Begin();
        restarter.Complete(outcome);

        outcome.Error.Should().Contain("no exe");
        _exitCodes.Should().BeEmpty();
        _lifetime.Verify(lifetime => lifetime.StopApplication(), Times.Never);
    }

    [Fact]
    public void SpawnThatDoesNotStart_ReportsAnError_AndNeverStopsTheServer()
    {
        ServerRestarter restarter = Restarter(_ => false);

        ServerRestartOutcome outcome = restarter.Begin();
        restarter.Complete(outcome);

        outcome.Error.Should().NotBeNullOrEmpty();
        _lifetime.Verify(lifetime => lifetime.StopApplication(), Times.Never);
    }

    [Fact]
    public void Complete_UnderASupervisor_ExitsWithTheRestartCode()
    {
        Restarter(_ => true).Complete(new("supervisor", null));

        _exitCodes.Should().Equal(75);
        _lifetime.Verify(lifetime => lifetime.StopApplication(), Times.Once);
    }

    [Fact]
    public void Complete_AfterARelaunch_StopsWithoutTheRestartCode()
    {
        Restarter(_ => true).Complete(new("relaunch", null));

        _exitCodes.Should().BeEmpty();
        _lifetime.Verify(lifetime => lifetime.StopApplication(), Times.Once);
    }

    private ServerRestarter Restarter(Func<ProcessStartInfo, bool> spawn) =>
        new(
            _lifetime.Object,
            _environment.GetValueOrDefault,
            () => _relaunch,
            startInfo =>
            {
                _spawned.Add(startInfo);
                return spawn(startInfo);
            },
            _exitCodes.Add
        );
}
