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

namespace NoMercy.App;

public static class BrowserControlParameters
{
    public const string RemoteDebugOptInVariable = "NOMERCY_APP_REMOTE_DEBUG";

    public static string? Resolve(bool isWindows, bool debuggerAttached, string? optIn)
    {
        if (!isWindows)
            return null;

        bool optedIn =
            optIn is not null
            && (optIn == "1" || optIn.Equals("true", StringComparison.OrdinalIgnoreCase));

        return debuggerAttached || optedIn ? "--remote-debugging-port=9222" : null;
    }
}
