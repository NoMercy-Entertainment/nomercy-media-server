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
using NoMercy.Plugins;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What the owner is told when a plugin reaches a member the contract does
/// not have.
/// <para>
/// This is the message the torrent downloader on a real server produced:
/// <c>Method not found: 'NoMercy.Events.IEventBus
/// IPluginContext.get_EventBus()'</c>. Accurate and useless. It names a
/// getter, not the capability the author has to declare instead, and it reads
/// as a server fault rather than a plugin built against something older.
/// </para>
/// </summary>
public class PluginStaleMemberMessageTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");

    [Fact]
    public void A_removed_member_is_described_as_a_refusal_not_as_a_missing_getter()
    {
        string described = PluginStaleMemberLog.Describe(
            Plugin,
            new MissingMethodException(
                "Method not found: 'NoMercy.Events.IEventBus NoMercy.Plugins.Abstractions.IPluginContext.get_EventBus()'."
            )
        );

        described.Should().Contain("get_EventBus", "the author has to know which member");
        described.Should().Contain("11.0", "and the version to rebuild against");
        described.Should().Contain("/nomercy-plugins/migration");
    }

    /// <summary>
    /// The host calls a plugin through delegates and tasks, so the failure
    /// arrives wrapped. Unwrapping is the difference between teaching and
    /// printing "One or more errors occurred".
    /// <para>
    /// The missing member is deliberately not the first inner exception. An
    /// aggregate hands back its first inner from <c>InnerException</c>, so a
    /// single-inner aggregate is walked by the ordinary chain whether or not
    /// anything searches the rest. Only a later sibling proves the search.
    /// </para>
    /// </summary>
    [Fact]
    public void A_removed_member_behind_another_failure_in_the_same_batch_is_found()
    {
        string described = PluginStaleMemberLog.Describe(
            Plugin,
            new AggregateException(
                new InvalidOperationException("the tracker refused the announce"),
                new MissingMethodException("Method not found: 'X IPluginContext.get_EventBus()'.")
            )
        );

        described.Should().Contain("get_EventBus");
        described.Should().Contain("11.0");
    }

    /// <summary>
    /// And the ordinary nesting the host actually produces: a task wrapping a
    /// plugin's own failure, with the missing member underneath it.
    /// </summary>
    [Fact]
    public void A_removed_member_nested_under_another_exception_is_found()
    {
        string described = PluginStaleMemberLog.Describe(
            Plugin,
            new InvalidOperationException(
                "starting the plugin",
                new MissingMethodException("Method not found: 'X IPluginContext.get_EventBus()'.")
            )
        );

        described.Should().Contain("get_EventBus");
    }

    /// <summary>
    /// Anything else keeps the message it had. A plugin whose own code threw
    /// must not be told to rebuild against a newer contract.
    /// </summary>
    [Fact]
    public void An_ordinary_failure_keeps_its_own_message()
    {
        string described = PluginStaleMemberLog.Describe(
            Plugin,
            new InvalidOperationException("the tracker refused the announce")
        );

        described.Should().Be("the tracker refused the announce");
        described.Should().NotContain("11.0");
    }
}
