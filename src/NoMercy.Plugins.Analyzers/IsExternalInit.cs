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

namespace System.Runtime.CompilerServices;

/// <summary>
/// What the compiler needs to emit an init-only setter, which netstandard2.0
/// does not ship. Records are worth the shim: the alternative is hand-written
/// equality on generated types.
/// </summary>
internal static class IsExternalInit { }
