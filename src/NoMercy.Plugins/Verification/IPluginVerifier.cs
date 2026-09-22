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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Verification;

public interface IPluginVerifier
{
    /// <summary>
    /// Judges a plugin before it is loaded or copied into place.
    /// <para>
    /// <paramref name="packagePath"/> is the artifact the server received — the
    /// .zip a repository published, or the file the owner uploaded — and it is
    /// the only thing <paramref name="expectedChecksum"/> ever describes.
    /// Leaving it out with a checksum supplied is refused rather than taken
    /// over the assembly instead, because one published checksum has to mean
    /// one file on every install path.
    /// </para>
    /// </summary>
    /// <param name="fromMarketplace">
    /// Whether a repository offered this, as opposed to the owner dropping the
    /// file in themselves. Signatures are enforced only for the first: the
    /// second is the owner's own decision about their own server.
    /// </param>
    PluginVerificationResult Verify(
        PluginManifest manifest,
        string assemblyPath,
        string? expectedChecksum,
        string? packagePath = null,
        bool fromMarketplace = false
    );
}
