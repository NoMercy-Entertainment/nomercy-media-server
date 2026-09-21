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

namespace NoMercy.Plugins.Runtime;

/// <summary>
/// Mapping a native library into the process, behind an interface because a
/// test that really loaded one could not unload it afterwards.
/// </summary>
public interface INativeLibraryLoader
{
    bool TryLoad(string path, out nint handle);
}

/// <summary>The real one.</summary>
public class SystemNativeLibraryLoader : INativeLibraryLoader
{
    public bool TryLoad(string path, out nint handle) => NativeLibrary.TryLoad(path, out handle);
}
