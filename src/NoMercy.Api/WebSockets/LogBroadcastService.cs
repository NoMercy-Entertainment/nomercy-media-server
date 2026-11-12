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
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Api.WebSockets;

public class LogBroadcastService(IClientMessenger clientMessenger) : ILogBroadcastService
{
    private readonly Lock _sync = new();
    private bool _broadcasting;

    public int ActiveSubscribers => _broadcasting ? 1 : 0;

    public void Start()
    {
        lock (_sync)
        {
            if (_broadcasting)
                return;
            _broadcasting = true;
            Logger.LogEmitted += OnLogEmitted;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_broadcasting)
                return;
            _broadcasting = false;
            Logger.LogEmitted -= OnLogEmitted;
        }
    }

    private void OnLogEmitted(LogEntry entry)
    {
        // Fire-and-forget: Logger.LogEmitted is a sync delegate; broadcasting is best-effort.
        _ = clientMessenger.SendToModerators("NewLog", "dashboardHub", entry);
    }
}
