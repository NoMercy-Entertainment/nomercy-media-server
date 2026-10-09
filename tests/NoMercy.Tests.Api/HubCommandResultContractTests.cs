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
using Microsoft.AspNetCore.SignalR;
using NoMercy.Api.Hubs;
using NoMercy.Networking;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Contract")]
public class HubCommandResultContractTests
{
    public static TheoryData<Type> Hubs =>
        new()
        {
            typeof(CastHub),
            typeof(ContentAnalysisHub),
            typeof(DashboardHub),
            typeof(DeviceHub),
            typeof(DrivesHub),
            typeof(LiveTranscodeHub),
            typeof(MusicHub),
            typeof(PluginHub),
            typeof(RipperHub),
            typeof(VideoHub),
        };

    [Theory]
    [MemberData(nameof(Hubs))]
    public void EveryVoidCommandReturnsAReadableResult(Type hubType)
    {
        MethodInfo[] commands = hubType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.DeclaringType != typeof(Hub))
            .Where(method => method.Name is not "OnConnectedAsync" and not "OnDisconnectedAsync")
            .Where(method => method.ReturnType == typeof(void) || method.ReturnType == typeof(Task))
            .ToArray();

        Assert.All(
            commands,
            method =>
                Assert.Fail(
                    $"{hubType.Name}.{method.Name} returns {method.ReturnType.Name}; "
                        + "it needs a readable command result"
                )
        );
    }
}
