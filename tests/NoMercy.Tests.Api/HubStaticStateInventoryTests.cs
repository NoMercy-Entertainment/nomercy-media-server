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
using NoMercy.Api.Hubs;
using Xunit;

namespace NoMercy.Tests.Api;

public class HubStaticStateInventoryTests
{
    [Theory]
    [InlineData(typeof(CastHub))]
    [InlineData(typeof(ContentAnalysisHub))]
    [InlineData(typeof(DashboardHub))]
    [InlineData(typeof(DeviceHub))]
    [InlineData(typeof(DrivesHub))]
    [InlineData(typeof(LiveTranscodeHub))]
    [InlineData(typeof(PluginHub))]
    [InlineData(typeof(RipperHub))]
    [InlineData(typeof(VideoHub))]
    public void HubHasNoStaticConnectionState(Type hubType)
    {
        FieldInfo[] fields = hubType.GetFields(
            BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly
        );

        Assert.Empty(fields);
    }
}
