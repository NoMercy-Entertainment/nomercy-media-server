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

namespace NoMercy.Plugin.Samples.NoPluginTypes;

/// <summary>
/// A clean IL assembly that holds no plugin type at all: what a manifest
/// points at by mistake, and what an archive test ships when the test is
/// about where files land rather than what runs.
/// </summary>
public sealed class Greeting
{
    public string Say(string name) => $"Hello, {name}";
}
