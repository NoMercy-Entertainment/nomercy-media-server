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

using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;

namespace NoMercy.Tests.NmSystem.Configuration;

/// <summary>
/// REQUIREMENT: a fresh install must never start trying to open itself to the internet
/// before an operator has chosen that. RuntimeServerSettings hydrates ConnectivityMode
/// from the Configuration table at boot (UserSettings), and a server with no row yet for
/// that key — every server on its first boot — keeps this class default untouched, so the
/// default alone decides the fresh-install behaviour. Fails if the default reverts to Auto
/// (or anything but LocalOnly), which would attempt UPnP/tunnel connectivity on first boot
/// without the operator ever making that choice.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConnectivityModeDefaultTests
{
    [Fact]
    public void ConnectivityMode_FreshInstance_DefaultsToLocalOnly()
    {
        RuntimeServerSettings freshInstall = new();

        freshInstall.ConnectivityMode.Should().Be(ConnectivityMode.LocalOnly);
    }
}
