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
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin that failed to load used to say nothing anywhere a person looks.
/// <para>
/// The reason went to the server log and was thrown away, so the dashboard,
/// the phone and the television could each say a plugin had malfunctioned and
/// none of them could say why. The owner is then left with a broken plugin, no
/// author to ask, and nothing to send them.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginMalfunctionReasonTests
{
    [Fact]
    public void AMalfunctioningPlugin_CarriesItsReasonToTheClient()
    {
        PluginInfo info = Info(PluginStatus.Malfunctioned);
        info.Malfunction =
            "The plugin called a member that does not exist: IPluginContext.get_EventBus().";

        PluginInfoDto dto = new(info);

        dto.Status.Should().Be("malfunctioned");
        dto.Malfunction.Should().Contain("get_EventBus");
    }

    /// <summary>
    /// Absent rather than empty on a healthy plugin, so no client has to
    /// decide whether an empty string means working.
    /// </summary>
    [Fact]
    public void AHealthyPlugin_CarriesNoReasonAtAll()
    {
        PluginInfoDto dto = new(Info(PluginStatus.Active));

        dto.Malfunction.Should().BeNull();
    }

    private static PluginInfo Info(PluginStatus status) =>
        new()
        {
            Id = Ulid.NewUlid(),
            Name = "Torrent Downloader",
            Description = "Downloads things.",
            Version = new Version(0, 6, 6),
            Status = status,
        };
}
