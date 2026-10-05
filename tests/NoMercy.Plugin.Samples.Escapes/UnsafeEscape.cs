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

namespace NoMercy.Plugin.Samples.Escapes;

public sealed class UnsafeEscape
{
    public unsafe byte Run()
    {
        byte* p = stackalloc byte[16];
        return Write(p);
    }

    // The pointer sits in a signature: a Release build drops the pointer local above.
    private static unsafe byte Write(byte* p)
    {
        p[0] = 1;
        return p[0];
    }
}
