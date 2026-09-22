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
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginLifecycleTests
{
    private static PluginInfo CreatePluginInfo(PluginStatus status)
    {
        return new()
        {
            Id = Ulid.NewUlid(),
            Name = "TestPlugin",
            Description = "Test",
            Version = new(1, 0, 0),
            Status = status,
        };
    }

    [Theory]
    [InlineData([PluginStatus.Active, PluginStatus.Disabled])]
    [InlineData([PluginStatus.Active, PluginStatus.Malfunctioned])]
    [InlineData([PluginStatus.Active, PluginStatus.Deleted])]
    [InlineData([PluginStatus.Disabled, PluginStatus.Active])]
    [InlineData([PluginStatus.Disabled, PluginStatus.Deleted])]
    [InlineData([PluginStatus.Malfunctioned, PluginStatus.Active])]
    [InlineData([PluginStatus.Malfunctioned, PluginStatus.Disabled])]
    [InlineData([PluginStatus.Malfunctioned, PluginStatus.Deleted])]
    public void CanTransition_AllowedTransitions_ReturnsTrue(PluginStatus from, PluginStatus to)
    {
        bool result = PluginLifecycle.CanTransition(from, to);

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData([PluginStatus.Deleted, PluginStatus.Active])]
    [InlineData([PluginStatus.Deleted, PluginStatus.Disabled])]
    [InlineData([PluginStatus.Deleted, PluginStatus.Malfunctioned])]
    [InlineData([PluginStatus.Disabled, PluginStatus.Malfunctioned])]
    [InlineData([PluginStatus.Active, PluginStatus.Active])]
    [InlineData([PluginStatus.Disabled, PluginStatus.Disabled])]
    public void CanTransition_ForbiddenTransitions_ReturnsFalse(PluginStatus from, PluginStatus to)
    {
        bool result = PluginLifecycle.CanTransition(from, to);

        result.Should().BeFalse();
    }

    [Fact]
    public void CanTransition_FromStatusAbsentFromTheAllowedTransitionMap_ReturnsFalse()
    {
        // Every real PluginStatus value is a key in AllowedTransitions, so the
        // TryGetValue-miss branch is otherwise unreachable through the public
        // enum. An out-of-range cast is the only way to exercise it — proving
        // CanTransition fails closed (denies) for a status the map has no entry
        // for, rather than throwing or defaulting to permissive.
        PluginStatus unknownStatus = (PluginStatus)999;

        bool result = PluginLifecycle.CanTransition(unknownStatus, PluginStatus.Active);

        result.Should().BeFalse();
    }

    [Fact]
    public void Transition_ValidTransition_UpdatesStatus()
    {
        PluginInfo info = CreatePluginInfo(PluginStatus.Active);

        PluginLifecycle.Transition(info, PluginStatus.Disabled);

        info.Status.Should().Be(PluginStatus.Disabled);
    }

    [Fact]
    public void Transition_InvalidTransition_ThrowsInvalidOperation()
    {
        PluginInfo info = CreatePluginInfo(PluginStatus.Deleted);

        Action act = () => PluginLifecycle.Transition(info, PluginStatus.Active);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Deleted*Active*");
    }

    [Fact]
    public void Transition_NullInfo_ThrowsArgumentNullException()
    {
        Action act = () => PluginLifecycle.Transition(null!, PluginStatus.Active);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Transition_ActiveToMalfunctioned_Succeeds()
    {
        PluginInfo info = CreatePluginInfo(PluginStatus.Active);

        PluginLifecycle.Transition(info, PluginStatus.Malfunctioned);

        info.Status.Should().Be(PluginStatus.Malfunctioned);
    }

    [Fact]
    public void Transition_MalfunctionedToActive_Succeeds()
    {
        PluginInfo info = CreatePluginInfo(PluginStatus.Malfunctioned);

        PluginLifecycle.Transition(info, PluginStatus.Active);

        info.Status.Should().Be(PluginStatus.Active);
    }

    [Fact]
    public void Transition_MalfunctionedToDisabled_Succeeds()
    {
        PluginInfo info = CreatePluginInfo(PluginStatus.Malfunctioned);

        PluginLifecycle.Transition(info, PluginStatus.Disabled);

        info.Status.Should().Be(PluginStatus.Disabled);
    }

    [Fact]
    public void Transition_DisabledToMalfunctioned_Fails()
    {
        PluginInfo info = CreatePluginInfo(PluginStatus.Disabled);

        Action act = () => PluginLifecycle.Transition(info, PluginStatus.Malfunctioned);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Transition_FullLifecycle_ActiveToDisabledToActiveToDeleted()
    {
        PluginInfo info = CreatePluginInfo(PluginStatus.Active);

        PluginLifecycle.Transition(info, PluginStatus.Disabled);
        info.Status.Should().Be(PluginStatus.Disabled);

        PluginLifecycle.Transition(info, PluginStatus.Active);
        info.Status.Should().Be(PluginStatus.Active);

        PluginLifecycle.Transition(info, PluginStatus.Deleted);
        info.Status.Should().Be(PluginStatus.Deleted);
    }

    [Fact]
    public void Transition_DeletedIsTerminal_CannotTransitionToAnything()
    {
        PluginInfo info = CreatePluginInfo(PluginStatus.Deleted);

        foreach (PluginStatus status in Enum.GetValues<PluginStatus>())
        {
            PluginLifecycle
                .CanTransition(PluginStatus.Deleted, status)
                .Should()
                .BeFalse($"Deleted should not transition to {status}");
        }
    }
}
