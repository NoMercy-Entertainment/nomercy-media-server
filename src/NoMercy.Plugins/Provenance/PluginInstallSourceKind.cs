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

namespace NoMercy.PluginSdk.Provenance;

/// <summary>
/// How an installed plugin reached this server. Strings rather than an enum:
/// they are written to the platform config and sent to clients as they are,
/// so the stored word and the word on the wire are one word.
/// </summary>
public static class PluginInstallSourceKind
{
    /// <summary>Downloaded from a repository's index by the server itself.</summary>
    public const string Repository = "repository";

    /// <summary>A .zip or .dll the owner uploaded, which needs developer mode.</summary>
    public const string File = "file";

    /// <summary>
    /// Installed before the server recorded where plugins came from, or put
    /// in the plugins folder by hand. Nothing about its source is known.
    /// </summary>
    public const string Unknown = "unknown";
}
