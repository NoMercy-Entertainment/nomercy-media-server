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

using System.Runtime.InteropServices;

namespace NoMercy.Plugin.Samples.Escapes;

public sealed partial class InteropEscape
{
    [DllImport("kernel32")]
    private static extern uint GetCurrentProcessId();

    public nint Run()
    {
        _ = GetCurrentProcessId();
        return Marshal.AllocHGlobal(1);
    }
}
