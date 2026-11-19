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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// The values of <see cref="PluginLibraryShow.AddedBy" />: why a show is in its
/// library. Added in ABI 12.13.
/// </summary>
public static class PluginLibraryAddedBy
{
    /// <summary>The owner added the show by hand, with or without a file yet.</summary>
    public const string Manual = "manual";

    /// <summary>A file on disk brought the show in: the watcher, the inbox or a scan.</summary>
    public const string File = "file";
}
