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
using NoMercy.Plugins.Sideload;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The refusal for a sideload says "turn on developer mode in server settings".
/// Nothing wrote that setting but a text editor, so the sentence sent the owner
/// somewhere that did not exist.
/// </summary>
[Trait("Category", "Unit")]
public class PluginDeveloperModeSwitchTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nm-devmode-{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ASwitchTheOwnerNeverTouched_IsOff()
    {
        new PluginDeveloperModeStore(_folder).Read().Enabled.Should().BeFalse();
    }

    [Fact]
    public void TurningItOn_IsWhatTheNextReadSees()
    {
        PluginDeveloperModeStore store = new(_folder);

        store.Write(true);

        store.Read().Enabled.Should().BeTrue();
    }

    [Fact]
    public void TurningItOffAgain_Sticks()
    {
        PluginDeveloperModeStore store = new(_folder);
        store.Write(true);

        store.Write(false);

        store.Read().Enabled.Should().BeFalse();
    }

    /// <summary>
    /// The install path reads the file on every call rather than a value it
    /// captured at boot, so turning the switch off closes the door now and not
    /// after a restart.
    /// </summary>
    [Fact]
    public void TheSourceTheInstallPathReads_FollowsTheSwitchWithoutARestart()
    {
        PluginDeveloperModeStore store = new(_folder);
        IPluginDeveloperModeSource source = new PluginDeveloperModeFile(_folder);

        store.Write(true);
        bool afterTurningOn = source.Enabled;

        store.Write(false);

        afterTurningOn.Should().BeTrue();
        source.Enabled.Should().BeFalse();
    }
}
