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

using System.Drawing;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// Pins <see cref="Logger.AddLogType"/>: the dashboard reads <see cref="Logger.LogTypes"/>
/// in order and opens each group at its spacer, so a type added at runtime must land in
/// a group of its own, after the built-in groups, without moving anything else.
/// Each test uses a group name of its own, because the table is process-wide.
/// </summary>
[Trait("Category", "Unit")]
public class LoggerLogTypesTests
{
    [Fact]
    public void AddLogType_InANewGroup_OpensThatGroupWithASpacerAtTheEnd()
    {
        string group = UniqueGroup();
        int before = Logger.LogTypes.Count;

        Logger.AddLogType(new("first", "First", Color.Lime, group));

        List<Logger.LogType> types = [.. Logger.LogTypes.Values];
        types.Should().HaveCount(before + 2);
        types[^2].Type.Should().Be("spacer");
        types[^2].DisplayName.Should().Be(group);
        types[^1].Name.Should().Be("first");
    }

    [Fact]
    public void AddLogType_InAGroupThatHasASpacer_AddsNoSecondSpacer()
    {
        string group = UniqueGroup();

        Logger.AddLogType(new($"{group}-a", "A", Color.Lime, group));
        Logger.AddLogType(new($"{group}-b", "B", Color.Lime, group));

        Logger
            .LogTypes.Values.Count(type => type.Type == "spacer" && type.DisplayName == group)
            .Should()
            .Be(1);
    }

    /// <summary>A spacer's name is its own key, so a new one must not reuse one.</summary>
    [Fact]
    public void AddLogType_GivesEveryNewSpacerAKeyNoOtherEntryHas()
    {
        Logger.AddLogType(new($"{UniqueGroup()}-a", "A", Color.Lime, UniqueGroup()));
        Logger.AddLogType(new($"{UniqueGroup()}-b", "B", Color.Lime, UniqueGroup()));

        List<Logger.LogType> spacers = [.. Logger.LogTypes.Values.Where(t => t.Type == "spacer")];
        spacers.Select(spacer => spacer.Name).Should().OnlyHaveUniqueItems();
        spacers.Select(spacer => spacer.DisplayName).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AddLogType_WithANameThatExists_ReplacesItWhereItIs()
    {
        string group = UniqueGroup();
        Logger.AddLogType(new($"{group}-renamed", "Old", Color.Lime, group));
        Logger.AddLogType(new($"{group}-after", "After", Color.Lime, group));
        List<string> order = [.. Logger.LogTypes.Keys];

        Logger.AddLogType(new($"{group}-renamed", "New", Color.Lime, group));

        Logger.LogTypes[$"{group}-renamed"].DisplayName.Should().Be("New");
        Logger.LogTypes.Keys.Should().Equal(order);
    }

    [Fact]
    public void AddLogType_LeavesTheBuiltInTypesInFront()
    {
        Logger.AddLogType(new($"{UniqueGroup()}-x", "X", Color.Lime, UniqueGroup()));

        Logger.LogTypes.Keys.First().Should().Be("_");
        Logger.LogTypes["app"].DisplayName.Should().Be("App");
    }

    private static string UniqueGroup() => $"Group {Guid.NewGuid():N}";
}
