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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// The code that leads every refusal the platform writes.
/// <para>
/// One code names one refusal everywhere it can appear — the server log, the
/// install response, the permissions page, the CLI and the marketplace scan —
/// so a publisher who searched for it finds one answer rather than five
/// wordings of it.
/// </para>
/// </summary>
public static class PluginRefusalCode
{
    /// <summary>The package arrived, and it is not the one the checksum describes.</summary>
    public const string ChecksumMismatch = "PLUGIN_CHECKSUM_MISMATCH";

    /// <summary>A checksum was published for something that is not a plugin package.</summary>
    public const string ChecksumSubjectNotAPackage = "PLUGIN_CHECKSUM_SUBJECT_NOT_A_PACKAGE";

    /// <summary>A checksum was supplied with no package to take it over.</summary>
    public const string ChecksumSubjectMissing = "PLUGIN_CHECKSUM_SUBJECT_MISSING";

    /// <summary>A caller reached a plugin route with its bearer token in the URL.</summary>
    public const string TokenInUrl = "PLUGIN_TOKEN_IN_URL";
}
