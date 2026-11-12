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

using Moq;
using NoMercy.Api.WebSockets;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.SystemCalls;
using Xunit;

namespace NoMercy.Tests.Api.WebSockets;

public class LogBroadcastServiceAudienceTests
{
    [Fact]
    public void LogEmitted_IsNotSentToEveryDashboardConnection()
    {
        Mock<IClientMessenger> messenger = new();
        LogBroadcastService service = new(messenger.Object);

        service.Start();
        try
        {
            Logger.App($"audience-probe-{Guid.NewGuid()}");
        }
        finally
        {
            service.Stop();
        }

        messenger.Verify(
            m => m.SendToAll("NewLog", It.IsAny<string>(), It.IsAny<object?>()),
            Times.Never
        );
        messenger.Verify(
            m => m.SendToModerators("NewLog", "dashboardHub", It.IsAny<object?>()),
            Times.AtLeastOnce
        );
    }
}
